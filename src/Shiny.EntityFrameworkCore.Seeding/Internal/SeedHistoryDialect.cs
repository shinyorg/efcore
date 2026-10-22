using System.Data.Common;
using System.Globalization;

namespace Shiny.EntityFrameworkCore.Seeding.Internal;

/// <summary>
/// The per-provider SQL for the <c>__ShinySeedHistory</c> table. The table is managed with raw DDL rather than
/// mapped into the user's model, so seeding needs no <c>OnModelCreating</c> call and no migration, and the table
/// can be read before (or without) the user's schema existing.
/// </summary>
/// <remarks>
/// Every statement uses the parameters <c>@name</c>, <c>@version</c> and <c>@applied</c>. A row whose
/// <c>AppliedAtUtc</c> is NULL is a lock placeholder written inside a seeding transaction; it is never committed
/// (the transaction either replaces it with a real row or rolls back), and readers ignore it.
/// </remarks>
abstract class SeedHistoryDialect
{
    public const string TableName = "__ShinySeedHistory";
    public const int MaxNameLength = 200;

    public static SeedHistoryDialect For(string? providerName) => providerName switch
    {
        "Microsoft.EntityFrameworkCore.Sqlite" => Sqlite.Instance,
        "Npgsql.EntityFrameworkCore.PostgreSQL" => Postgres.Instance,
        "Microsoft.EntityFrameworkCore.SqlServer" => SqlServer.Instance,
        "MySql.EntityFrameworkCore" => MySql.Instance,
        _ => throw new NotSupportedException(
            $"Shiny seeding does not support the EF Core provider '{providerName}'. Supported providers: " +
            "SQLite, PostgreSQL (Npgsql), SQL Server and MySQL (MySql.EntityFrameworkCore).")
    };

    /// <summary>Returns a scalar that is non-zero/true when the table exists. Must not create anything.</summary>
    public abstract string TableExistsSql { get; }

    /// <summary>Creates the table if it is missing.</summary>
    public abstract string CreateTableSql { get; }

    /// <summary>Reads Version + AppliedAtUtc for <c>@name</c>, without locking.</summary>
    public virtual string SelectSql => $"SELECT {this.Q("Version")}, {this.Q("AppliedAtUtc")} FROM {this.Table} WHERE {this.Q("Name")} = @name";

    /// <summary>Reads Version + AppliedAtUtc for every applied row.</summary>
    public virtual string SelectAllSql => $"SELECT {this.Q("Name")}, {this.Q("Version")}, {this.Q("AppliedAtUtc")} FROM {this.Table} WHERE {this.Q("AppliedAtUtc")} IS NOT NULL ORDER BY {this.Q("Name")}";

    /// <summary>
    /// Takes an exclusive lock on the row for <c>@name</c> for the rest of the transaction, inserting a
    /// placeholder when it does not exist yet, so a concurrent seeding transaction for the same name waits here.
    /// </summary>
    public abstract string LockSql { get; }

    /// <summary>Reads Version + AppliedAtUtc for <c>@name</c> after <see cref="LockSql"/>, seeing the latest committed row.</summary>
    public virtual string SelectLockedSql => this.SelectSql;

    /// <summary>Upserts <c>@name</c>/<c>@version</c>/<c>@applied</c>, never lowering a recorded version.</summary>
    public abstract string UpsertSql { get; }

    public virtual string DeleteSql => $"DELETE FROM {this.Table} WHERE {this.Q("Name")} = @name";

    public string Table => this.Q(TableName);

    protected abstract string Q(string identifier);

    /// <summary>Converts a timestamp into the value this provider's driver binds to the AppliedAtUtc column.</summary>
    public virtual object ToDbValue(DateTimeOffset value) => value.UtcDateTime;

    /// <summary>Converts the AppliedAtUtc column value back into a timestamp.</summary>
    public static DateTimeOffset FromDbValue(object value) => value switch
    {
        DateTimeOffset dto => dto.ToUniversalTime(),
        DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
        string s => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
        _ => throw new InvalidOperationException($"Unexpected AppliedAtUtc value type '{value.GetType()}'.")
    };

    public static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    sealed class Sqlite : SeedHistoryDialect
    {
        public static readonly Sqlite Instance = new();

        public override string TableExistsSql => $"SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '{TableName}'";

        public override string CreateTableSql =>
            $"CREATE TABLE IF NOT EXISTS {this.Table} ({this.Q("Name")} TEXT NOT NULL PRIMARY KEY, {this.Q("Version")} INTEGER NOT NULL, {this.Q("AppliedAtUtc")} TEXT NULL)";

        // SQLite has one writer per database: the first write in a transaction takes the database write lock, so
        // touching the row is the lock.
        public override string LockSql =>
            $"INSERT INTO {this.Table} ({this.Q("Name")}, {this.Q("Version")}, {this.Q("AppliedAtUtc")}) VALUES (@name, 0, NULL) " +
            $"ON CONFLICT ({this.Q("Name")}) DO UPDATE SET {this.Q("Name")} = excluded.{this.Q("Name")}";

        public override string UpsertSql =>
            $"INSERT INTO {this.Table} ({this.Q("Name")}, {this.Q("Version")}, {this.Q("AppliedAtUtc")}) VALUES (@name, @version, @applied) " +
            $"ON CONFLICT ({this.Q("Name")}) DO UPDATE SET {this.Q("Version")} = excluded.{this.Q("Version")}, {this.Q("AppliedAtUtc")} = excluded.{this.Q("AppliedAtUtc")} " +
            $"WHERE {this.Table}.{this.Q("AppliedAtUtc")} IS NULL OR {this.Table}.{this.Q("Version")} <= excluded.{this.Q("Version")}";

        // ISO-8601 text sorts and round-trips; SQLite has no timestamp type.
        public override object ToDbValue(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

        protected override string Q(string identifier) => $"\"{identifier}\"";
    }

    sealed class Postgres : SeedHistoryDialect
    {
        public static readonly Postgres Instance = new();

        // to_regclass resolves the name exactly like the unqualified statements below (through search_path).
        public override string TableExistsSql => $"SELECT to_regclass('{this.Table}') IS NOT NULL";

        public override string CreateTableSql =>
            $"CREATE TABLE IF NOT EXISTS {this.Table} ({this.Q("Name")} varchar({MaxNameLength}) NOT NULL PRIMARY KEY, {this.Q("Version")} integer NOT NULL, {this.Q("AppliedAtUtc")} timestamp with time zone NULL)";

        // DO UPDATE (unlike DO NOTHING) row-locks an existing row; an in-flight insert by another transaction makes
        // this wait for that transaction to finish.
        public override string LockSql =>
            $"INSERT INTO {this.Table} ({this.Q("Name")}, {this.Q("Version")}, {this.Q("AppliedAtUtc")}) VALUES (@name, 0, NULL) " +
            $"ON CONFLICT ({this.Q("Name")}) DO UPDATE SET {this.Q("Name")} = EXCLUDED.{this.Q("Name")}";

        public override string UpsertSql =>
            $"INSERT INTO {this.Table} ({this.Q("Name")}, {this.Q("Version")}, {this.Q("AppliedAtUtc")}) VALUES (@name, @version, @applied) " +
            $"ON CONFLICT ({this.Q("Name")}) DO UPDATE SET {this.Q("Version")} = EXCLUDED.{this.Q("Version")}, {this.Q("AppliedAtUtc")} = EXCLUDED.{this.Q("AppliedAtUtc")} " +
            $"WHERE {this.Table}.{this.Q("AppliedAtUtc")} IS NULL OR {this.Table}.{this.Q("Version")} <= EXCLUDED.{this.Q("Version")}";

        protected override string Q(string identifier) => $"\"{identifier}\"";
    }

    sealed class SqlServer : SeedHistoryDialect
    {
        public static readonly SqlServer Instance = new();

        public override string TableExistsSql => $"SELECT CASE WHEN OBJECT_ID(N'{TableName}', N'U') IS NULL THEN 0 ELSE 1 END";

        public override string CreateTableSql =>
            $"IF OBJECT_ID(N'{TableName}', N'U') IS NULL CREATE TABLE {this.Table} ({this.Q("Name")} nvarchar({MaxNameLength}) NOT NULL PRIMARY KEY, {this.Q("Version")} int NOT NULL, {this.Q("AppliedAtUtc")} datetimeoffset NULL)";

        // UPDLOCK + SERIALIZABLE holds a key-range lock even when the row is missing, so a second transaction blocks
        // on the UPDATE instead of racing to the INSERT.
        public override string LockSql =>
            $"UPDATE {this.Table} WITH (UPDLOCK, SERIALIZABLE) SET {this.Q("Name")} = {this.Q("Name")} WHERE {this.Q("Name")} = @name; " +
            $"IF @@ROWCOUNT = 0 INSERT INTO {this.Table} ({this.Q("Name")}, {this.Q("Version")}, {this.Q("AppliedAtUtc")}) VALUES (@name, 0, NULL);";

        public override string UpsertSql =>
            $"UPDATE {this.Table} WITH (UPDLOCK, SERIALIZABLE) SET {this.Q("Version")} = @version, {this.Q("AppliedAtUtc")} = @applied " +
            $"WHERE {this.Q("Name")} = @name AND ({this.Q("AppliedAtUtc")} IS NULL OR {this.Q("Version")} <= @version); " +
            $"IF @@ROWCOUNT = 0 AND NOT EXISTS (SELECT 1 FROM {this.Table} WITH (UPDLOCK, SERIALIZABLE) WHERE {this.Q("Name")} = @name) " +
            $"INSERT INTO {this.Table} ({this.Q("Name")}, {this.Q("Version")}, {this.Q("AppliedAtUtc")}) VALUES (@name, @version, @applied);";

        public override object ToDbValue(DateTimeOffset value) => value.ToUniversalTime();

        protected override string Q(string identifier) => $"[{identifier}]";
    }

    sealed class MySql : SeedHistoryDialect
    {
        public static readonly MySql Instance = new();

        public override string TableExistsSql =>
            $"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = '{TableName}'";

        public override string CreateTableSql =>
            $"CREATE TABLE IF NOT EXISTS {this.Table} ({this.Q("Name")} varchar({MaxNameLength}) NOT NULL PRIMARY KEY, {this.Q("Version")} int NOT NULL, {this.Q("AppliedAtUtc")} datetime(6) NULL)";

        // ON DUPLICATE KEY UPDATE takes an exclusive lock on the existing row (INSERT IGNORE would take a shared one,
        // and two shared holders upgrading is the classic InnoDB deadlock).
        public override string LockSql =>
            $"INSERT INTO {this.Table} ({this.Q("Name")}, {this.Q("Version")}, {this.Q("AppliedAtUtc")}) VALUES (@name, 0, NULL) " +
            $"ON DUPLICATE KEY UPDATE {this.Q("Name")} = {this.Q("Name")}";

        // REPEATABLE READ would otherwise read the transaction's snapshot; a locking read sees the latest commit.
        public override string SelectLockedSql => this.SelectSql + " FOR UPDATE";

        // MySQL applies ON DUPLICATE KEY assignments left to right, so AppliedAtUtc compares against the new Version.
        public override string UpsertSql =>
            $"INSERT INTO {this.Table} ({this.Q("Name")}, {this.Q("Version")}, {this.Q("AppliedAtUtc")}) VALUES (@name, @version, @applied) " +
            $"ON DUPLICATE KEY UPDATE " +
            $"{this.Q("Version")} = IF({this.Q("AppliedAtUtc")} IS NULL OR {this.Q("Version")} <= VALUES({this.Q("Version")}), VALUES({this.Q("Version")}), {this.Q("Version")}), " +
            $"{this.Q("AppliedAtUtc")} = IF({this.Q("Version")} = VALUES({this.Q("Version")}), VALUES({this.Q("AppliedAtUtc")}), {this.Q("AppliedAtUtc")})";

        protected override string Q(string identifier) => $"`{identifier}`";
    }
}
