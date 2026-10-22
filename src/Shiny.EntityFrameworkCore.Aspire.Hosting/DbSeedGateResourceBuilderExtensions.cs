using Aspire.Hosting.ApplicationModel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shiny.EntityFrameworkCore.Aspire.Hosting.Internal;
using Shiny.EntityFrameworkCore.Seeding;

namespace Shiny.EntityFrameworkCore.Aspire.Hosting;

/// <summary>
/// AppHost seed gates for database resources: seed on first-time setup or on destructive recreation, never on an
/// ordinary restart.
/// </summary>
public static class DbSeedGateResourceBuilderExtensions
{
    const string LoggerCategory = "Shiny.EntityFrameworkCore.Aspire.Seeder";

    /// <summary>
    /// When <paramref name="database"/> is ready, builds a <typeparamref name="TContext"/> against it, brings the
    /// schema up (<paramref name="initialization"/>, every start), and runs <paramref name="seed"/> on exactly two
    /// triggers: the first time the database is set up, and, in <see cref="DbSeedMode.Recreate"/> mode, every later
    /// start after dropping the database (<c>EnsureDeleted</c>) and rebuilding it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The "already seeded" marker is a row in <c>__ShinySeedHistory</c> inside the database itself, written only after
    /// the callback (and a <c>SaveChangesAsync</c> for anything it left pending) succeeds. Dropping the container volume
    /// drops the marker, so the next start is a first-time setup again.
    /// </para>
    /// <para>
    /// The context is created with <paramref name="configure"/> and the AppHost's services (<c>ActivatorUtilities</c>),
    /// so a context with the usual <c>(DbContextOptions&lt;TContext&gt;)</c> constructor works as-is. The AppHost
    /// needs a reference to the project that defines <typeparamref name="TContext"/> (mark it
    /// <c>IsAspireProjectResource="false"</c> if it is not a service the AppHost runs) and to its EF provider.
    /// </para>
    /// </remarks>
    /// <param name="database">A database resource, e.g. <c>AddPostgres("pg").AddDatabase("app")</c>.</param>
    /// <param name="configure">Points the options at the connection string, e.g. <c>(o, cs) =&gt; o.UseNpgsql(cs)</c>.</param>
    /// <param name="seed">Writes the seed data. Running <c>DbSeedRunner.RunAsync</c> here reuses your app's seeders.</param>
    /// <param name="mode">First-time only (default) or recreate on every start.</param>
    /// <param name="initialization">
    /// How the schema is created: <see cref="DbInitialization.Migrate"/> (default) or
    /// <see cref="DbInitialization.EnsureCreated"/>. <see cref="DbInitialization.None"/> leaves it to the callback and
    /// is not allowed with <see cref="DbSeedMode.Recreate"/>, which drops the whole database.
    /// </param>
    public static IResourceBuilder<IResourceWithConnectionString> WithDbContextSeeder<TContext>(
        this IResourceBuilder<IResourceWithConnectionString> database,
        Action<DbContextOptionsBuilder<TContext>, string> configure,
        Func<TContext, DbSeedGateContext, CancellationToken, Task> seed,
        DbSeedMode mode = DbSeedMode.FirstTimeOnly,
        DbInitialization initialization = DbInitialization.Migrate)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(configure);
        ArgumentNullException.ThrowIfNull(seed);
        if (mode == DbSeedMode.Recreate && initialization == DbInitialization.None)
            throw new ArgumentException(
                "DbSeedMode.Recreate drops the database, so it needs DbInitialization.Migrate or EnsureCreated to rebuild it.",
                nameof(initialization));

        var resource = database.Resource;
        database.ApplicationBuilder.Eventing.Subscribe<ResourceReadyEvent>(resource, async (@event, ct) =>
        {
            var connectionString = await ResolveConnectionStringAsync(resource, ct).ConfigureAwait(false);
            var logger = @event.Services.GetRequiredService<ILoggerFactory>().CreateLogger(LoggerCategory);

            await DbSeedGate.RunAsync(
                resource.Name,
                connectionString,
                () =>
                {
                    var options = new DbContextOptionsBuilder<TContext>();
                    configure(options, connectionString);
                    return ActivatorUtilities.CreateInstance<TContext>(@event.Services, options.Options);
                },
                mode,
                initialization,
                seed,
                logger,
                ct).ConfigureAwait(false);
        });

        return database;
    }

    /// <summary>
    /// The raw gate, for seeding that doesn't go through a <c>DbContext</c> of yours: when <paramref name="database"/>
    /// is ready, runs <paramref name="seed"/> with the connection string on first-time setup and, in
    /// <see cref="DbSeedMode.Recreate"/> mode, on every later start with <see cref="DbSeedGateContext.Recreate"/> set,
    /// in which case the callback owns the wipe.
    /// </summary>
    /// <remarks>
    /// The marker still lives in the database, reached through a model-less <c>DbContext</c> that
    /// <paramref name="configureMarkerConnection"/> points at the connection string (e.g.
    /// <c>(o, cs) =&gt; o.UseNpgsql(cs)</c>), so the package needs no ADO driver of its own. The marker table counts as
    /// "a table" to <c>EnsureCreated</c>: if your app creates its schema that way, create it in this callback or use
    /// <see cref="WithDbContextSeeder{TContext}"/>.
    /// </remarks>
    public static IResourceBuilder<IResourceWithConnectionString> WithDbSeeder(
        this IResourceBuilder<IResourceWithConnectionString> database,
        Action<DbContextOptionsBuilder, string> configureMarkerConnection,
        Func<DbSeedGateContext, CancellationToken, Task> seed,
        DbSeedMode mode = DbSeedMode.FirstTimeOnly)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(configureMarkerConnection);
        ArgumentNullException.ThrowIfNull(seed);

        var resource = database.Resource;
        database.ApplicationBuilder.Eventing.Subscribe<ResourceReadyEvent>(resource, async (@event, ct) =>
        {
            var connectionString = await ResolveConnectionStringAsync(resource, ct).ConfigureAwait(false);
            var logger = @event.Services.GetRequiredService<ILoggerFactory>().CreateLogger(LoggerCategory);

            await DbSeedGate.RunAsync(
                resource.Name,
                connectionString,
                () =>
                {
                    var options = new DbContextOptionsBuilder();
                    configureMarkerConnection(options, connectionString);
                    return new DbContext(options.Options);
                },
                mode,
                seed,
                logger,
                ct).ConfigureAwait(false);
        });

        return database;
    }

    static async Task<string> ResolveConnectionStringAsync(IResourceWithConnectionString resource, CancellationToken ct)
    {
        var connectionString = await resource.GetConnectionStringAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(connectionString))
            throw new InvalidOperationException($"The connection string for database resource '{resource.Name}' was null or empty.");
        return connectionString;
    }
}
