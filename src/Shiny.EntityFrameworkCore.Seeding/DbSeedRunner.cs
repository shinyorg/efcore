using Microsoft.EntityFrameworkCore;
using Shiny.EntityFrameworkCore.Seeding.Internal;

namespace Shiny.EntityFrameworkCore.Seeding;

/// <summary>
/// Runs <see cref="IDbSeeder{TContext}"/>s against a context with versioned run-once semantics. The hosted service
/// (<c>AddDbSeeder</c>) and EF's own seeding hook (<c>UseVersionedSeeding</c>) both call this; call it directly
/// where there is no generic host (MAUI) or to seed on your own schedule.
/// </summary>
public static class DbSeedRunner
{
    /// <summary>
    /// Runs, in the order given, each seeder that has never run or whose <see cref="IDbSeeder{TContext}.Version"/>
    /// is higher than its recorded marker, and returns the names of the seeders that actually ran.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each seeder runs in its own transaction: the seed writes, a <c>SaveChangesAsync</c> for anything left
    /// pending, and the marker row commit together, so a seeder that throws leaves neither data nor marker and is
    /// retried next time. Seeders before it keep their markers. The transaction goes through the context's
    /// execution strategy, so retrying strategies (e.g. <c>EnableRetryOnFailure</c>) work.
    /// </para>
    /// <para>
    /// If the context already has a transaction (one you began, or the one EF holds around seeding during
    /// <c>EnsureCreated</c>), seeders join it instead, and committing is the caller's job.
    /// </para>
    /// <para>
    /// Two processes seeding the same database at once are serialized on the marker row: the second waits for the
    /// first to commit, re-reads the marker, and skips. Seeders should still be idempotent: a bumped version
    /// re-runs over existing data, and MySQL commits DDL implicitly, so a seeder that issues DDL there is not atomic.
    /// </para>
    /// <para>
    /// Use a context with no unrelated pending changes: the runner saves whatever the context is tracking.
    /// </para>
    /// </remarks>
    public static async Task<IReadOnlyList<string>> RunAsync<TContext>(
        TContext context,
        IEnumerable<IDbSeeder<TContext>> seeders,
        CancellationToken cancellationToken = default)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(seeders);

        var list = seeders.ToList();
        var applied = new List<string>();
        if (list.Count > 0)
        {
            ValidateSeeders(list);
            await SeedHistoryStore.EnsureTableAsync(context, cancellationToken).ConfigureAwait(false);

            foreach (var seeder in list)
            {
                // Fast path: the common startup case is "everything already applied", which needs no lock and no write.
                var marker = await SeedHistoryStore.FindAsync(context, seeder.Name, false, cancellationToken).ConfigureAwait(false);
                var ran = !IsApplied(marker, seeder) && await RunOneAsync(context, seeder, cancellationToken).ConfigureAwait(false);
                if (ran)
                    applied.Add(seeder.Name);
            }
        }
        return applied;
    }

    static Task<bool> RunOneAsync<TContext>(TContext context, IDbSeeder<TContext> seeder, CancellationToken ct)
        where TContext : DbContext
    {
        if (context.Database.CurrentTransaction != null)
            return SeedUnderLockAsync(context, seeder, ct);

        var strategy = context.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(
            (context, seeder),
            static async (_, state, ct) =>
            {
                var (ctx, sdr) = state;
                await using var transaction = await ctx.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
                try
                {
                    var ran = await SeedUnderLockAsync(ctx, sdr, ct).ConfigureAwait(false);
                    await transaction.CommitAsync(ct).ConfigureAwait(false);
                    return ran;
                }
                catch
                {
                    // Whatever the seeder added is rolled back in the database; drop it from the tracker too, so a
                    // retry (or the caller's next SaveChanges) doesn't resurrect it.
                    ctx.ChangeTracker.Clear();
                    throw;
                }
            },
            null,
            ct);
    }

    static async Task<bool> SeedUnderLockAsync<TContext>(TContext context, IDbSeeder<TContext> seeder, CancellationToken ct)
        where TContext : DbContext
    {
        await SeedHistoryStore.LockAsync(context, seeder.Name, ct).ConfigureAwait(false);

        // Re-read under the lock: another process may have applied this seeder while we were deciding.
        var marker = await SeedHistoryStore.FindAsync(context, seeder.Name, true, ct).ConfigureAwait(false);
        var run = !IsApplied(marker, seeder);
        if (run)
        {
            await seeder.SeedAsync(context, ct).ConfigureAwait(false);
            if (context.ChangeTracker.HasChanges())
                await context.SaveChangesAsync(ct).ConfigureAwait(false);

            await SeedHistoryStore.UpsertAsync(context, seeder.Name, seeder.Version, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        }
        return run;
    }

    static bool IsApplied<TContext>(DbSeedRecord? marker, IDbSeeder<TContext> seeder) where TContext : DbContext
        => marker != null && marker.Version >= seeder.Version;

    static void ValidateSeeders<TContext>(List<IDbSeeder<TContext>> seeders) where TContext : DbContext
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var seeder in seeders)
        {
            ArgumentNullException.ThrowIfNull(seeder, nameof(seeders));
            DbSeedHistory.ValidateName(seeder.Name);
            if (!names.Add(seeder.Name))
                throw new ArgumentException($"Two seeders share the name '{seeder.Name}'; names key the history and must be unique (case-insensitively, because SQL Server and MySQL compare them that way).", nameof(seeders));
        }
    }
}
