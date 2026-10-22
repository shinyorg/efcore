using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Shiny.EntityFrameworkCore.Seeding;

namespace Shiny.EntityFrameworkCore.Aspire.Hosting.Internal;

/// <summary>
/// Decides whether an AppHost seed callback runs on this start, and on which trigger. Split out of the Aspire eventing
/// subscription so the decision is testable without starting an application.
/// </summary>
/// <remarks>
/// The marker is a row in the Seeding package's <c>__ShinySeedHistory</c> table, keyed <c>aspire:&lt;resource&gt;</c>
/// (plus the context type for the EF overload), read and written through an EF context on the database's own
/// connection. So the gate needs no ADO driver of its own, and a dropped volume takes the marker with it.
/// </remarks>
static class DbSeedGate
{

    public static string MarkerName(string resourceName, Type? contextType)
        => contextType == null ? $"aspire:{resourceName}" : $"aspire:{resourceName}:{contextType.Name}";

    /// <summary>The EF overload: the gate owns the schema (migrate/EnsureCreated) and, in Recreate mode, the wipe.</summary>
    public static async Task<bool> RunAsync<TContext>(
        string resourceName,
        string connectionString,
        Func<TContext> createContext,
        DbSeedMode mode,
        DbInitialization initialization,
        Func<TContext, DbSeedGateContext, CancellationToken, Task> seed,
        ILogger logger,
        CancellationToken ct)
        where TContext : DbContext
    {
        var markerName = MarkerName(resourceName, typeof(TContext));
        await using var context = createContext();

        var marker = await ReadMarkerAsync(context, markerName, ct).ConfigureAwait(false);
        var recreate = marker != null && mode == DbSeedMode.Recreate;
        if (recreate)
        {
            logger.LogWarning(
                "Recreating database '{Resource}' for {Context}: it was seeded at {SeededAtUtc} and the seeder is in {Mode} mode. The database is being dropped.",
                resourceName,
                typeof(TContext).Name,
                marker!.AppliedAtUtc,
                mode);
            await context.Database.EnsureDeletedAsync(ct).ConfigureAwait(false);
        }

        // Every start brings the schema up to date (a new migration lands without a volume drop); seeding is gated.
        await InitializeAsync(context, initialization, ct).ConfigureAwait(false);

        var run = marker == null || recreate;
        if (run)
        {
            if (!recreate)
                logger.LogInformation("Seeding database '{Resource}' for {Context} for the first time", resourceName, typeof(TContext).Name);

            await seed(context, new DbSeedGateContext(resourceName, connectionString, recreate), ct).ConfigureAwait(false);
            if (context.ChangeTracker.HasChanges())
                await context.SaveChangesAsync(ct).ConfigureAwait(false);

            // Written only on success, so a failed seed is retried on the next start rather than silently skipped.
            await DbSeedHistory.RecordAsync(context, markerName, 1, ct).ConfigureAwait(false);
            logger.LogInformation("Seeder completed for database '{Resource}' ({Context})", resourceName, typeof(TContext).Name);
        }
        else
        {
            logger.LogInformation(
                "Database '{Resource}' was already seeded for {Context} at {SeededAtUtc}; skipping (mode {Mode})",
                resourceName,
                typeof(TContext).Name,
                marker!.AppliedAtUtc,
                mode);
        }
        return run;
    }

    /// <summary>The raw overload: the callback owns schema and wipe; the gate owns only the marker.</summary>
    public static async Task<bool> RunAsync(
        string resourceName,
        string connectionString,
        Func<DbContext> createMarkerContext,
        DbSeedMode mode,
        Func<DbSeedGateContext, CancellationToken, Task> seed,
        ILogger logger,
        CancellationToken ct)
    {
        var markerName = MarkerName(resourceName, null);
        DbSeedRecord? marker;
        await using (var probe = createMarkerContext())
            marker = await ReadMarkerAsync(probe, markerName, ct).ConfigureAwait(false);

        var run = marker == null || mode == DbSeedMode.Recreate;
        if (run)
        {
            var recreate = marker != null;
            if (recreate)
                logger.LogWarning(
                    "Recreating database '{Resource}': it was seeded at {SeededAtUtc} and the seeder is in {Mode} mode. The seed callback owns the wipe.",
                    resourceName,
                    marker!.AppliedAtUtc,
                    mode);
            else
                logger.LogInformation("Seeding database '{Resource}' for the first time", resourceName);

            await seed(new DbSeedGateContext(resourceName, connectionString, recreate), ct).ConfigureAwait(false);

            // A fresh context: the callback may have dropped and recreated the database under the old one.
            await using var context = createMarkerContext();
            if (!await context.GetService<IRelationalDatabaseCreator>().ExistsAsync(ct).ConfigureAwait(false))
                throw new InvalidOperationException(
                    $"The seed callback for '{resourceName}' left no database behind, so the seed marker cannot be written. Recreate the database in the callback.");

            await DbSeedHistory.RecordAsync(context, markerName, 1, ct).ConfigureAwait(false);
            logger.LogInformation("Seeder completed for database '{Resource}'", resourceName);
        }
        else
        {
            logger.LogInformation(
                "Database '{Resource}' was already seeded at {SeededAtUtc}; skipping (mode {Mode})",
                resourceName,
                marker!.AppliedAtUtc,
                mode);
        }
        return run;
    }

    static async Task<DbSeedRecord?> ReadMarkerAsync(DbContext context, string markerName, CancellationToken ct)
    {
        // A database that doesn't exist yet (or was dropped) has no marker: that is a first-time setup.
        var exists = await context.GetService<IRelationalDatabaseCreator>().ExistsAsync(ct).ConfigureAwait(false);
        return exists ? await DbSeedHistory.FindAsync(context, markerName, ct).ConfigureAwait(false) : null;
    }

    static Task InitializeAsync(DbContext context, DbInitialization initialization, CancellationToken ct) => initialization switch
    {
        DbInitialization.Migrate => context.Database.MigrateAsync(ct),
        DbInitialization.EnsureCreated => context.Database.EnsureCreatedAsync(ct),
        _ => Task.CompletedTask
    };
}
