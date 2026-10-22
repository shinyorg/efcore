using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace Shiny.EntityFrameworkCore.Temporal.Internal;

/// <summary>A change captured from the change tracker, waiting to be written as a history version.</summary>
sealed class PendingChange
{
    public required EntityEntry Entry { get; init; }
    public required HistoryMapping Mapping { get; init; }
    public required TemporalOperation Operation { get; set; }

    /// <summary>Captured before the save for updates and removals (keys are known and a deleted entry is detached afterwards); null for inserts, whose key may be store-generated.</summary>
    public string? Key { get; init; }
}

/// <summary>
/// Appends history versions with plain parameterized SQL through EF's relational command pipeline (so it logs,
/// runs command interceptors and enlists in the current transaction), not through LINQ: it runs inside
/// <c>SaveChanges</c> (at transaction commit), where starting a query on the context would trip EF's concurrency
/// detector. Identifiers, parameter placeholders and parameter types all come from the provider's own services,
/// which is what keeps it provider-agnostic.
/// </summary>
static class HistoryWriter
{
    public static async Task WriteAsync(DbContext context, IReadOnlyList<PendingChange> changes, TemporalHistoryOptions options, bool async, CancellationToken ct)
    {
        var table = HistoryTable.Resolve(context);
        var now = Truncate(options.TimeProvider.GetUtcNow().UtcDateTime);
        var actor = ResolveActor(context, options);
        if (actor is { Length: > HistoryAnnotations.MaxActorLength })
            throw new InvalidOperationException($"Actor '{actor}' is longer than {HistoryAnnotations.MaxActorLength} characters.");

        foreach (var change in changes)
        {
            var key = change.Key ?? KeyFormatter.FromEntry(change.Entry, original: false);
            if (key.Length > HistoryAnnotations.MaxKeyLength)
                throw new InvalidOperationException(
                    $"The history key '{key}' of '{change.Entry.Metadata.DisplayName()}' is longer than {HistoryAnnotations.MaxKeyLength} characters.");

            var snapshot = change.Operation == TemporalOperation.Removed
                ? null
                : Snapshot.Write(change.Entry, change.Mapping.MappedType, options.SerializerOptions);

            var current = await table.ExecuteScalarAsync(context, table.MaxVersionSql, new()
            {
                ["entityType"] = change.Mapping.Name,
                ["entityKey"] = key
            }, async, ct).ConfigureAwait(false);
            var previous = current is null or DBNull ? 0L : Convert.ToInt64(current, System.Globalization.CultureInfo.InvariantCulture);
            var version = previous + 1;

            var parameters = new Dictionary<string, object?>
            {
                ["entityType"] = change.Mapping.Name,
                ["entityKey"] = key,
                ["version"] = version,
                ["validFrom"] = now,
                ["operation"] = change.Operation,
                ["actor"] = actor,
                ["snapshot"] = snapshot
            };

            // Close the previous version by its exact primary key: a point lock only, so concurrent writers of
            // neighbouring entities can't deadlock on gap/range locks (InnoDB under REPEATABLE READ).
            var sql = table.InsertSql;
            if (previous > 0)
            {
                sql = table.CloseSql + sql;
                parameters["previous"] = previous;
            }
            if (change.Mapping.Retention is { } retention)
            {
                sql += table.PruneByAgeSql;
                parameters["cutoff"] = Truncate(now - retention);
            }
            if (change.Mapping.MaxVersions is { } max)
            {
                // Versions are monotonic per entity, so "keep the newest N" is "drop everything at or below new - N".
                sql += table.PruneByCountSql;
                parameters["keepAbove"] = version - max;
            }

            await table.ExecuteNonQueryAsync(context, sql, parameters, async, ct).ConfigureAwait(false);
        }
    }

    static string? ResolveActor(DbContext context, TemporalHistoryOptions options)
    {
        if (options.ResolveActor != null)
        {
            var services = context.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()?.ApplicationServiceProvider;
            if (services != null)
                return options.ResolveActor(services);
        }
        return options.CaptureActor?.Invoke();
    }

    /// <summary>Microsecond precision: the finest every supported provider stores (MySQL / PostgreSQL), so a written instant round-trips exactly.</summary>
    public static DateTime Truncate(DateTime utc)
        => new(utc.Ticks - (utc.Ticks % 10), DateTimeKind.Utc);
}

/// <summary>The history table's SQL, built once per context from the model and the provider's SQL helper.</summary>
sealed class HistoryTable
{
    readonly Dictionary<string, (string Name, RelationalTypeMapping Mapping)> parameters = new();

    HistoryTable(DbContext context)
    {
        var entityType = context.Model.FindEntityType(typeof(EntityHistoryRecord))
            ?? throw new InvalidOperationException($"'{nameof(EntityHistoryRecord)}' is not in the model. Call HasHistory() on at least one entity.");
        var tableName = entityType.GetTableName()
            ?? throw new InvalidOperationException($"'{nameof(EntityHistoryRecord)}' must be mapped to a table.");
        var store = StoreObjectIdentifier.Table(tableName, entityType.GetSchema());
        var helper = context.GetService<ISqlGenerationHelper>();

        string Column(string property) => helper.DelimitIdentifier(entityType.FindProperty(property)!.GetColumnName(store)!);
        string Param(string name, string property)
        {
            var parameterName = helper.GenerateParameterName(name);
            this.parameters[name] = (parameterName, entityType.FindProperty(property)!.GetRelationalTypeMapping());
            return helper.GenerateParameterNamePlaceholder(name);
        }

        var table = helper.DelimitIdentifier(tableName, entityType.GetSchema());
        var et = Column(nameof(EntityHistoryRecord.EntityType));
        var ek = Column(nameof(EntityHistoryRecord.EntityKey));
        var version = Column(nameof(EntityHistoryRecord.Version));
        var validFrom = Column(nameof(EntityHistoryRecord.ValidFrom));
        var validTo = Column(nameof(EntityHistoryRecord.ValidTo));
        var match = $"{et} = {Param("entityType", nameof(EntityHistoryRecord.EntityType))} AND {ek} = {Param("entityKey", nameof(EntityHistoryRecord.EntityKey))}";

        this.MaxVersionSql = $"SELECT MAX({version}) FROM {table} WHERE {match}";

        this.CloseSql =
            $"UPDATE {table} SET {validTo} = {Param("validFrom", nameof(EntityHistoryRecord.ValidFrom))} WHERE {match} AND {version} = {Param("previous", nameof(EntityHistoryRecord.Version))};\n";
        this.InsertSql =
            $"INSERT INTO {table} ({et}, {ek}, {version}, {validFrom}, {validTo}, {Column(nameof(EntityHistoryRecord.Operation))}, {Column(nameof(EntityHistoryRecord.Actor))}, {Column(nameof(EntityHistoryRecord.Snapshot))}) " +
            $"VALUES ({Param("entityType", nameof(EntityHistoryRecord.EntityType))}, {Param("entityKey", nameof(EntityHistoryRecord.EntityKey))}, {Param("version", nameof(EntityHistoryRecord.Version))}, " +
            $"{Param("validFrom", nameof(EntityHistoryRecord.ValidFrom))}, NULL, {Param("operation", nameof(EntityHistoryRecord.Operation))}, {Param("actor", nameof(EntityHistoryRecord.Actor))}, {Param("snapshot", nameof(EntityHistoryRecord.Snapshot))});\n";

        this.PruneByAgeSql = $"DELETE FROM {table} WHERE {match} AND {validTo} IS NOT NULL AND {validTo} < {Param("cutoff", nameof(EntityHistoryRecord.ValidFrom))};\n";
        this.PruneByCountSql = $"DELETE FROM {table} WHERE {match} AND {version} <= {Param("keepAbove", nameof(EntityHistoryRecord.Version))};\n";
    }

    public string MaxVersionSql { get; }
    public string CloseSql { get; }
    public string InsertSql { get; }
    public string PruneByAgeSql { get; }
    public string PruneByCountSql { get; }

    // One instance per model; the SQL depends only on the model and the provider, both fixed for a model.
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IModel, HistoryTable> Cache = new();

    public static HistoryTable Resolve(DbContext context)
        => Cache.GetValue(context.Model, _ => new HistoryTable(context));

    public async Task<object?> ExecuteScalarAsync(DbContext context, string sql, Dictionary<string, object?> values, bool async, CancellationToken ct)
    {
        var (command, parameterObject) = this.Build(context, sql, values);
        return async
            ? await command.ExecuteScalarAsync(parameterObject, ct).ConfigureAwait(false)
            : command.ExecuteScalar(parameterObject);
    }

    public async Task ExecuteNonQueryAsync(DbContext context, string sql, Dictionary<string, object?> values, bool async, CancellationToken ct)
    {
        var (command, parameterObject) = this.Build(context, sql, values);
        if (async)
            await command.ExecuteNonQueryAsync(parameterObject, ct).ConfigureAwait(false);
        else
            command.ExecuteNonQuery(parameterObject);
    }

    (IRelationalCommand, RelationalCommandParameterObject) Build(DbContext context, string sql, Dictionary<string, object?> values)
    {
        var builder = context.GetService<IRelationalCommandBuilderFactory>().Create().Append(sql);
        foreach (var name in values.Keys)
        {
            var (parameterName, mapping) = this.parameters[name];
            builder.AddParameter(name, parameterName, mapping, nullable: true);
        }

        var parameterObject = new RelationalCommandParameterObject(
            context.GetService<IRelationalConnection>(),
            values,
            null,
            context,
            context.GetService<IRelationalCommandDiagnosticsLogger>(),
            CommandSource.SaveChanges);
        return (builder.Build(), parameterObject);
    }
}
