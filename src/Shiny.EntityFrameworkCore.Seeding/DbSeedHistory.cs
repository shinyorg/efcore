using Microsoft.EntityFrameworkCore;
using Shiny.EntityFrameworkCore.Seeding.Internal;

namespace Shiny.EntityFrameworkCore.Seeding;

/// <summary>
/// Direct access to the <c>__ShinySeedHistory</c> table that <see cref="DbSeedRunner"/> keeps in the context's
/// own database: inspect what has run, record a marker yourself, or forget one so its seeder runs again.
/// <para>
/// The table is created with raw DDL the first time a marker is written; reads never create it (an empty
/// database stays empty, which matters because <c>EnsureCreated</c> does nothing once any table exists).
/// Supported providers: SQLite, PostgreSQL, SQL Server and MySQL.
/// </para>
/// </summary>
public static class DbSeedHistory
{
    /// <summary>The table name, <c>__ShinySeedHistory</c>, created in the connection's default schema.</summary>
    public const string TableName = SeedHistoryDialect.TableName;

    /// <summary>Returns every applied marker, ordered by name. Empty when the table does not exist yet.</summary>
    public static Task<IReadOnlyList<DbSeedRecord>> GetAllAsync(DbContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return SeedHistoryStore.GetAllAsync(context, cancellationToken);
    }

    /// <summary>Returns the marker for <paramref name="name"/>, or null when that name has never been recorded.</summary>
    public static Task<DbSeedRecord?> FindAsync(DbContext context, string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return SeedHistoryStore.FindAsync(context, name, false, cancellationToken);
    }

    /// <summary>
    /// Records <paramref name="name"/> at <paramref name="version"/>, creating the table if needed. An existing
    /// higher version is kept (a marker never goes backwards, so an older app instance can't undo a newer one).
    /// Runs inside the context's current transaction when there is one.
    /// </summary>
    public static async Task RecordAsync(DbContext context, string name, int version, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ValidateName(name);
        await SeedHistoryStore.EnsureTableAsync(context, cancellationToken).ConfigureAwait(false);
        await SeedHistoryStore.UpsertAsync(context, name, version, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes the marker for <paramref name="name"/> so its seeder runs on the next pass. Returns false when
    /// there was nothing to delete.
    /// </summary>
    public static Task<bool> RemoveAsync(DbContext context, string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return SeedHistoryStore.DeleteAsync(context, name, cancellationToken);
    }

    internal static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > SeedHistoryDialect.MaxNameLength)
            throw new ArgumentException(
                $"Seeder name '{name}' is {name.Length} characters; the history key holds at most {SeedHistoryDialect.MaxNameLength}.",
                nameof(name));
    }
}
