using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Shiny.EntityFrameworkCore.Seeding.Internal;

/// <summary>Lets the (non-generic) hosted service drive one context type.</summary>
interface IDbSeedContextRunner
{
    Task RunAsync(IServiceProvider scopedServices, CancellationToken ct);
}

sealed class DbSeedContextRunner<TContext>(DbSeedingConfiguration<TContext> configuration) : IDbSeedContextRunner
    where TContext : DbContext
{
    public async Task RunAsync(IServiceProvider scopedServices, CancellationToken ct)
    {
        var (context, owned) = ResolveContext(scopedServices);
        try
        {
            switch (configuration.Options.Initialization)
            {
                case DbInitialization.Migrate:
                    await context.Database.MigrateAsync(ct).ConfigureAwait(false);
                    break;

                case DbInitialization.EnsureCreated:
                    await context.Database.EnsureCreatedAsync(ct).ConfigureAwait(false);
                    break;
            }

            var seeders = ResolveSeeders(scopedServices);
            await DbSeedRunner.RunAsync(context, seeders, ct).ConfigureAwait(false);
        }
        finally
        {
            if (owned)
                await context.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal static List<IDbSeeder<TContext>> ResolveSeeders(IServiceProvider scopedServices)
        => scopedServices
            .GetServices<DbSeederRegistration<TContext>>()
            .Select(x => x.Resolve(scopedServices))
            .ToList();

    static (TContext Context, bool Owned) ResolveContext(IServiceProvider services)
    {
        var context = services.GetService<TContext>();
        if (context != null)
            return (context, false);

        var factory = services.GetService<IDbContextFactory<TContext>>()
            ?? throw new InvalidOperationException(
                $"Seeding needs a '{typeof(TContext).Name}': register it with AddDbContext or AddDbContextFactory.");

        return (factory.CreateDbContext(), true);
    }
}
