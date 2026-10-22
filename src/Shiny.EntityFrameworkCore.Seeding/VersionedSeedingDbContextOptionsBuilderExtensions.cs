using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Shiny.EntityFrameworkCore.Seeding.Internal;

namespace Shiny.EntityFrameworkCore.Seeding;

/// <summary>
/// Plugs the versioned runner into EF Core's own seeding hook, so <c>Database.Migrate()</c> and
/// <c>Database.EnsureCreated()</c> (sync or async) run your seeders with run-once markers instead of re-running
/// <c>UseSeeding</c> code on every call.
/// </summary>
public static class VersionedSeedingDbContextOptionsBuilderExtensions
{
    /// <summary>
    /// Runs <paramref name="seeders"/> through <see cref="DbSeedRunner"/> whenever EF seeds (after
    /// <c>Migrate</c>/<c>EnsureCreated</c>). Pass no seeders to use the ones registered with <c>AddDbSeeder</c>,
    /// resolved from a scope of the application service provider (that needs a DI-created context).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This wires both <c>UseAsyncSeeding</c> and <c>UseSeeding</c>; the sync path blocks on the async runner.
    /// A seeding delegate configured before this call runs first; one configured after it replaces this one.
    /// </para>
    /// <para>
    /// If EF has a transaction open while seeding, the seeders join it; otherwise each runs in its own. Combining
    /// this with the hosted service is safe: whichever path runs second finds the markers and does nothing.
    /// </para>
    /// </remarks>
    public static DbContextOptionsBuilder<TContext> UseVersionedSeeding<TContext>(
        this DbContextOptionsBuilder<TContext> builder,
        params IDbSeeder<TContext>[] seeders)
        where TContext : DbContext
    {
        ((DbContextOptionsBuilder)builder).UseVersionedSeeding(seeders);
        return builder;
    }

    /// <inheritdoc cref="UseVersionedSeeding{TContext}(DbContextOptionsBuilder{TContext}, IDbSeeder{TContext}[])"/>
    public static DbContextOptionsBuilder UseVersionedSeeding<TContext>(
        this DbContextOptionsBuilder builder,
        params IDbSeeder<TContext>[] seeders)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(seeders);

        var explicitSeeders = seeders.ToArray();
        var core = builder.Options.FindExtension<CoreOptionsExtension>();
        var previousAsync = core?.AsyncSeeder;
        var previousSync = core?.Seeder;

        builder.UseAsyncSeeding(async (context, storeManagementPerformed, ct) =>
        {
            if (previousAsync != null)
                await previousAsync(context, storeManagementPerformed, ct).ConfigureAwait(false);

            await RunAsync(context, explicitSeeders, ct).ConfigureAwait(false);
        });

        builder.UseSeeding((context, storeManagementPerformed) =>
        {
            previousSync?.Invoke(context, storeManagementPerformed);
            RunAsync(context, explicitSeeders, CancellationToken.None).GetAwaiter().GetResult();
        });

        return builder;
    }

    static async Task RunAsync<TContext>(DbContext context, IDbSeeder<TContext>[] explicitSeeders, CancellationToken ct)
        where TContext : DbContext
    {
        if (context is not TContext typed)
            throw new InvalidOperationException(
                $"UseVersionedSeeding<{typeof(TContext).Name}> was configured on a '{context.GetType().Name}'.");

        if (explicitSeeders.Length > 0)
        {
            await DbSeedRunner.RunAsync(typed, explicitSeeders, ct).ConfigureAwait(false);
        }
        else
        {
            var services = context
                .GetService<IDbContextOptions>()
                .FindExtension<CoreOptionsExtension>()?
                .ApplicationServiceProvider
                ?? throw new InvalidOperationException(
                    $"UseVersionedSeeding() with no seeders runs the AddDbSeeder<{typeof(TContext).Name}, ...> registrations, " +
                    "which needs a context created by dependency injection (AddDbContext/AddDbContextFactory). Pass the seeders explicitly instead.");

            await using var scope = services.CreateAsyncScope();
            var seeders = DbSeedContextRunner<TContext>.ResolveSeeders(scope.ServiceProvider);
            await DbSeedRunner.RunAsync(typed, seeders, ct).ConfigureAwait(false);
        }
    }
}
