using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Shiny.EntityFrameworkCore.Seeding.Internal;

/// <summary>
/// Raw ADO access to <c>__ShinySeedHistory</c> over the context's own connection and current transaction. Going
/// through <see cref="DatabaseFacade.OpenConnectionAsync"/>/<see cref="DatabaseFacade.CloseConnectionAsync"/>
/// keeps EF's connection reference counting intact, so a connection EF (or the caller) already opened stays open.
/// </summary>
static class SeedHistoryStore
{
    public static async Task<bool> TableExistsAsync(DbContext context, CancellationToken ct)
    {
        var dialect = SeedHistoryDialect.For(context.Database.ProviderName);
        return await WithConnectionAsync(context, async () =>
        {
            await using var command = CreateCommand(context, dialect.TableExistsSql);
            var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return value is not null and not DBNull && Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates the table when it is missing. Two processes creating it at once can make the loser's DDL fail
    /// (PostgreSQL's catalog unique index, SQL Server's "object already exists"); when that happens outside a
    /// transaction and the table now exists, the failure is benign and swallowed.
    /// </summary>
    public static async Task EnsureTableAsync(DbContext context, CancellationToken ct)
    {
        var dialect = SeedHistoryDialect.For(context.Database.ProviderName);
        var exists = await TableExistsAsync(context, ct).ConfigureAwait(false);
        if (!exists)
        {
            try
            {
                await ExecuteAsync(context, dialect.CreateTableSql, null, ct).ConfigureAwait(false);
            }
            catch (DbException) when (context.Database.CurrentTransaction == null)
            {
                if (!await TableExistsAsync(context, ct).ConfigureAwait(false))
                    throw;
            }
        }
    }

    /// <summary>Reads the applied row for <paramref name="name"/>; null when missing, a placeholder, or the table doesn't exist.</summary>
    public static async Task<DbSeedRecord?> FindAsync(DbContext context, string name, bool locked, CancellationToken ct)
    {
        var dialect = SeedHistoryDialect.For(context.Database.ProviderName);
        var exists = locked || await TableExistsAsync(context, ct).ConfigureAwait(false);
        if (!exists)
            return null;

        return await WithConnectionAsync(context, async () =>
        {
            await using var command = CreateCommand(context, locked ? dialect.SelectLockedSql : dialect.SelectSql);
            SeedHistoryDialect.AddParameter(command, "@name", name);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

            DbSeedRecord? record = null;
            if (await reader.ReadAsync(ct).ConfigureAwait(false) && !reader.IsDBNull(1))
            {
                record = new DbSeedRecord(
                    name,
                    Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture),
                    SeedHistoryDialect.FromDbValue(reader.GetValue(1)));
            }
            return record;
        }, ct).ConfigureAwait(false);
    }

    public static async Task<IReadOnlyList<DbSeedRecord>> GetAllAsync(DbContext context, CancellationToken ct)
    {
        var dialect = SeedHistoryDialect.For(context.Database.ProviderName);
        if (!await TableExistsAsync(context, ct).ConfigureAwait(false))
            return [];

        return await WithConnectionAsync<IReadOnlyList<DbSeedRecord>>(context, async () =>
        {
            await using var command = CreateCommand(context, dialect.SelectAllSql);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

            var list = new List<DbSeedRecord>();
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                list.Add(new DbSeedRecord(
                    reader.GetString(0),
                    Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture),
                    SeedHistoryDialect.FromDbValue(reader.GetValue(2))));
            }
            return list;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Locks (inserting a placeholder if needed) the row for <paramref name="name"/> until the transaction ends.</summary>
    public static Task LockAsync(DbContext context, string name, CancellationToken ct)
        => ExecuteAsync(
            context,
            SeedHistoryDialect.For(context.Database.ProviderName).LockSql,
            command => SeedHistoryDialect.AddParameter(command, "@name", name),
            ct);

    public static Task UpsertAsync(DbContext context, string name, int version, DateTimeOffset appliedAtUtc, CancellationToken ct)
    {
        var dialect = SeedHistoryDialect.For(context.Database.ProviderName);
        return ExecuteAsync(
            context,
            dialect.UpsertSql,
            command =>
            {
                SeedHistoryDialect.AddParameter(command, "@name", name);
                SeedHistoryDialect.AddParameter(command, "@version", version);
                SeedHistoryDialect.AddParameter(command, "@applied", dialect.ToDbValue(appliedAtUtc));
            },
            ct);
    }

    public static async Task<bool> DeleteAsync(DbContext context, string name, CancellationToken ct)
    {
        var dialect = SeedHistoryDialect.For(context.Database.ProviderName);
        if (!await TableExistsAsync(context, ct).ConfigureAwait(false))
            return false;

        var rows = await ExecuteAsync(
            context,
            dialect.DeleteSql,
            command => SeedHistoryDialect.AddParameter(command, "@name", name),
            ct).ConfigureAwait(false);
        return rows > 0;
    }

    static Task<int> ExecuteAsync(DbContext context, string sql, Action<DbCommand>? parameters, CancellationToken ct)
        => WithConnectionAsync(context, async () =>
        {
            await using var command = CreateCommand(context, sql);
            parameters?.Invoke(command);
            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, ct);

    static async Task<T> WithConnectionAsync<T>(DbContext context, Func<Task<T>> work, CancellationToken ct)
    {
        await context.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            return await work().ConfigureAwait(false);
        }
        finally
        {
            await context.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    static DbCommand CreateCommand(DbContext context, string sql)
    {
        var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
        var timeout = context.Database.GetCommandTimeout();
        if (timeout.HasValue)
            command.CommandTimeout = timeout.Value;
        return command;
    }
}
