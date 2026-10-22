using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using NetTopologySuite.Geometries;
using Shiny.EntityFrameworkCore.Sqlite.Mobile.Infrastructure;

namespace Shiny.EntityFrameworkCore.Sqlite.Mobile.Sidecars;

/// <summary>
/// For every spatial / vector / full-text index in the model, adds a keyless, migration-excluded entity type mapped
/// onto the sidecar virtual table (so LINQ can address it in sub-queries), then validates the configuration once the
/// model is finalized so a bad mapping fails at startup rather than at the first query.
/// </summary>
sealed class SidecarConvention : IModelFinalizingConvention, IModelFinalizedConvention
{
    readonly SqliteMobileFeatures features;

    public SidecarConvention(SqliteMobileFeatures features) => this.features = features;

    public void ProcessModelFinalizing(IConventionModelBuilder modelBuilder, IConventionContext<IConventionModelBuilder> context)
    {
        foreach (var entityType in modelBuilder.Metadata.GetEntityTypes().Where(e => !SidecarModel.IsSidecarEntity(e)).ToList())
        {
            var table = entityType.GetTableName();
            var keyType = entityType.FindPrimaryKey() is { Properties.Count: 1 } pk ? pk.Properties[0].ClrType : typeof(long);
            if (table != null)
            {
                var store = StoreObjectIdentifier.Table(table, entityType.GetSchema());
                foreach (var property in entityType.GetDeclaredProperties())
                {
                    var column = property.GetColumnName(store) ?? property.Name;
                    if (SidecarModel.HasSpatialIndex(property))
                    {
                        var name = SidecarModel.EntityName(SidecarModel.SpatialName(table, column));
                        property.Builder.HasAnnotation(SidecarAnnotationNames.SidecarEntityName, name);
                        AddRTree(modelBuilder, name, SidecarModel.SpatialName(table, column), keyType);
                    }

                    if (SidecarModel.VectorDimensions(property) != null)
                    {
                        var name = SidecarModel.EntityName(SidecarModel.VectorName(table, column));
                        property.Builder.HasAnnotation(SidecarAnnotationNames.SidecarEntityName, name);
                        AddVector(modelBuilder, name, SidecarModel.VectorName(table, column), keyType);
                    }
                }

                if (SidecarModel.FullTextProperties(entityType) != null)
                {
                    var name = SidecarModel.EntityName(SidecarModel.FullTextName(table));
                    entityType.Builder.HasAnnotation(SidecarAnnotationNames.SidecarEntityName, name);
                    AddFullText(modelBuilder, name, SidecarModel.FullTextName(table), keyType);
                }
            }
        }
    }

    public IModel ProcessModelFinalized(IModel model)
    {
        foreach (var entityType in model.GetEntityTypes().Where(e => !SidecarModel.IsSidecarEntity(e)))
        {
            foreach (var property in entityType.GetDeclaredProperties())
            {
                if (SidecarModel.HasSpatialIndex(property))
                {
                    if (!this.features.Spatial)
                        throw Error($"'{Describe(property)}' has a spatial index, but spatial support is not enabled. Call UseShinySpatial() inside UseSqlite(...).");

                    if (!typeof(Geometry).IsAssignableFrom(property.ClrType))
                        throw Error($"'{Describe(property)}' has a spatial index, but its type '{property.ClrType.Name}' is not a NetTopologySuite Geometry.");

                    ValidateRowIdTable(entityType, "spatial", Describe(property));
                }

                var dims = SidecarModel.VectorDimensions(property);
                if (dims != null)
                {
                    if (!this.features.Vectors)
                        throw Error($"'{Describe(property)}' has a vector index, but vector support is not enabled. Call UseShinyVectors() inside UseSqlite(...).");

                    if (property.ClrType != typeof(float[]) && property.ClrType != typeof(ReadOnlyMemory<float>) && property.ClrType != typeof(ReadOnlyMemory<float>?))
                        throw Error($"'{Describe(property)}' has a vector index, but its type '{property.ClrType.Name}' is not float[] or ReadOnlyMemory<float>.");

                    if (dims.Value <= 0)
                        throw Error($"'{Describe(property)}' has a vector index with {dims.Value} dimensions; dimensions must be positive.");

                    ValidateRowIdTable(entityType, "vector", Describe(property));
                }
            }

            var fts = SidecarModel.FullTextProperties(entityType);
            if (fts != null)
            {
                if (!this.features.FullText)
                    throw Error($"'{entityType.DisplayName()}' has a full-text index, but full-text support is not enabled. Call UseShinyFullText() inside UseSqlite(...).");

                if (fts.Count == 0)
                    throw Error($"The full-text index on '{entityType.DisplayName()}' has no properties.");

                foreach (var name in fts)
                {
                    var property = entityType.FindProperty(name)
                        ?? throw Error($"The full-text index on '{entityType.DisplayName()}' references '{name}', which is not a mapped property.");

                    if (property.ClrType != typeof(string))
                        throw Error($"The full-text index on '{entityType.DisplayName()}' includes '{name}' of type '{property.ClrType.Name}'; only string properties can be indexed.");

                    var table = entityType.GetTableName();
                    if (table != null && property.GetColumnName(StoreObjectIdentifier.Table(table, entityType.GetSchema())) == null)
                        throw Error($"The full-text index on '{entityType.DisplayName()}' includes '{name}', which is not stored in table '{table}'.");
                }

                ValidateRowIdTable(entityType, "full-text", entityType.DisplayName());
            }
        }

        return model;
    }

    // Sidecars are keyed by rowid. Only an INTEGER PRIMARY KEY column is a stable rowid alias: the implicit rowid of
    // any other table can be renumbered by VACUUM, silently detaching every sidecar row from its entity.
    static void ValidateRowIdTable(IEntityType entityType, string kind, string subject)
    {
        if (entityType.IsOwned())
            throw Error($"The {kind} index on '{subject}' is on an owned entity type; indexes must be on an entity type with its own table.");

        var table = entityType.GetTableName()
            ?? throw Error($"The {kind} index on '{subject}' requires '{entityType.DisplayName()}' to be mapped to a table (views, functions and SQL queries are not supported).");

        if (entityType.GetMappingStrategy() == RelationalAnnotationNames.TpcMappingStrategy)
            throw Error($"The {kind} index on '{subject}' is not supported with TPC (table-per-concrete-type) inheritance, because the property is spread over several tables.");

        var key = entityType.FindPrimaryKey();
        var keyProperty = key is { Properties.Count: 1 } ? key.Properties[0] : null;
        var columnType = keyProperty?.GetColumnType(StoreObjectIdentifier.Table(table, entityType.GetSchema()));
        var clr = keyProperty == null ? null : Nullable.GetUnderlyingType(keyProperty.ClrType) ?? keyProperty.ClrType;
        var integral = clr == typeof(long) || clr == typeof(int) || clr == typeof(short) || clr == typeof(byte);

        if (keyProperty == null || !integral || !string.Equals(columnType, "INTEGER", StringComparison.OrdinalIgnoreCase))
        {
            var found = key == null
                ? "no primary key"
                : string.Join(", ", key.Properties.Select(p => $"{p.ClrType.Name} {p.Name}"));

            throw Error(
                $"The {kind} index on '{subject}' requires '{entityType.DisplayName()}' to have a single integer primary key stored as an " +
                $"INTEGER column (a SQLite rowid alias); found {found}. Sidecar tables are keyed by rowid, and the implicit rowid of a " +
                "table without an INTEGER PRIMARY KEY is not stable (VACUUM may renumber it). WITHOUT ROWID tables are not supported.");
        }
    }

    static void AddRTree(IConventionModelBuilder modelBuilder, string entityName, string table, Type keyType)
    {
        var builder = AddSidecar(modelBuilder, entityName, table, SidecarKind.RTree);
        if (builder != null)
        {
            Column(builder, keyType, "Id", "id");
            Column(builder, typeof(double), "MinX", "minx");
            Column(builder, typeof(double), "MaxX", "maxx");
            Column(builder, typeof(double), "MinY", "miny");
            Column(builder, typeof(double), "MaxY", "maxy");
        }
    }

    static void AddVector(IConventionModelBuilder modelBuilder, string entityName, string table, Type keyType)
    {
        var builder = AddSidecar(modelBuilder, entityName, table, SidecarKind.Vector);
        if (builder != null)
        {
            Column(builder, keyType, "RowId", "rowid");
            Column(builder, typeof(byte[]), "Embedding", "embedding");
            Column(builder, typeof(double), "Distance", "distance");
            Column(builder, typeof(int), "K", "k");
        }
    }

    static void AddFullText(IConventionModelBuilder modelBuilder, string entityName, string table, Type keyType)
    {
        var builder = AddSidecar(modelBuilder, entityName, table, SidecarKind.FullText);
        if (builder != null)
        {
            Column(builder, keyType, "RowId", "rowid");
            // FTS5's hidden column named after the table is the MATCH target.
            Column(builder, typeof(string), "Match", table);
            Column(builder, typeof(double), "Rank", "rank");
        }
    }

    static IConventionEntityTypeBuilder? AddSidecar(IConventionModelBuilder modelBuilder, string entityName, string table, SidecarKind kind)
    {
        if (modelBuilder.Metadata.FindEntityType(entityName) != null)
            return null;

        // Added as "data annotation" configuration: EF's ModelCleanupConvention removes convention-sourced entity
        // types that no navigation reaches, which is exactly what a sidecar is.
        var builder = modelBuilder.SharedTypeEntity(entityName, typeof(Dictionary<string, object>), shouldBeOwned: false, fromDataAnnotation: true)!;
        builder.HasNoKey(fromDataAnnotation: true);
        builder.ToTable(table, fromDataAnnotation: true);
        builder.ExcludeTableFromMigrations(true, fromDataAnnotation: true);
        builder.HasAnnotation(SidecarAnnotationNames.SidecarEntity, kind.ToString(), fromDataAnnotation: true);
        return builder;
    }

    static void Column(IConventionEntityTypeBuilder builder, Type type, string property, string column)
        => builder.IndexerProperty(type, property, fromDataAnnotation: true)!.HasColumnName(column, fromDataAnnotation: true);

    static string Describe(IReadOnlyProperty property) => $"{property.DeclaringType.DisplayName()}.{property.Name}";

    static InvalidOperationException Error(string message) => new(message);
}
