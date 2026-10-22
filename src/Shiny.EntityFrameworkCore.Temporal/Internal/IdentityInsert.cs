using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace Shiny.EntityFrameworkCore.Temporal.Internal;

/// <summary>
/// Saves a restore that re-inserts a removed entity under its original key. Every provider accepts an explicit
/// value for an auto-increment / identity key except SQL Server, which needs <c>SET IDENTITY_INSERT ... ON</c> on
/// the same connection for the duration of the insert.
/// </summary>
static class IdentityInsert
{
    const string SqlServerProvider = "Microsoft.EntityFrameworkCore.SqlServer";
    const string ValueGenerationStrategy = "SqlServer:ValueGenerationStrategy";

    public static async Task SaveAsync(DbContext context, IEntityType entityType, CancellationToken ct)
    {
        var root = entityType.GetRootType();
        if (context.Database.ProviderName != SqlServerProvider || !HasIdentityKey(root))
        {
            await context.SaveChangesAsync(ct).ConfigureAwait(false);
            return;
        }

        // The table name comes from the model and is delimited by the provider, never from user input.
#pragma warning disable EF1002
        var table = context.GetService<ISqlGenerationHelper>().DelimitIdentifier(root.GetTableName()!, root.GetSchema());
        await context.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            await context.Database.ExecuteSqlRawAsync($"SET IDENTITY_INSERT {table} ON", ct).ConfigureAwait(false);
            try
            {
                await context.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                await context.Database.ExecuteSqlRawAsync($"SET IDENTITY_INSERT {table} OFF", ct).ConfigureAwait(false);
            }
        }
#pragma warning restore EF1002
        finally
        {
            await context.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    // Read by annotation name so the package needs no SQL Server reference. The strategy is normally stamped on
    // the property; when it isn't, an integer key generated on add inherits the model-wide default (identity).
    static bool HasIdentityKey(IEntityType entityType)
        => entityType.FindPrimaryKey()!.Properties.Any(p =>
            (p.FindAnnotation(ValueGenerationStrategy) ?? (IsIntegerGeneratedOnAdd(p) ? entityType.Model.FindAnnotation(ValueGenerationStrategy) : null))
                ?.Value?.ToString() == "IdentityColumn");

    static bool IsIntegerGeneratedOnAdd(IProperty property)
    {
        var type = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
        return property.ValueGenerated == ValueGenerated.OnAdd
            && (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte) || type == typeof(decimal))
            && property.GetDefaultValueSql() == null
            && property.GetComputedColumnSql() == null;
    }
}
