using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Sqlite.Metadata.Internal;

namespace Shiny.EntityFrameworkCore.Sqlite.Mobile.Sidecars;

/// <summary>
/// Projects the model's index annotations onto the relational <see cref="ITable"/> as <c>Shiny:Sidecar:*</c>
/// annotations. Because they are table annotations, the migrations differ compares them like any other table
/// change: EnsureCreated sees them on <c>CreateTableOperation</c>, and adding/removing/changing an index yields an
/// <c>AlterTableOperation</c> carrying both the old and new definitions.
/// </summary>
sealed class SidecarRelationalAnnotationProvider(RelationalAnnotationProviderDependencies dependencies)
    : SqliteAnnotationProvider(dependencies)
{
    public override IEnumerable<IAnnotation> For(ITable table, bool designTime)
    {
        foreach (var annotation in base.For(table, designTime))
            yield return annotation;

        // Only the design-time relational model feeds DDL (EnsureCreated, the differ and the migrator all use it);
        // the read-optimized runtime model doesn't carry the migration-exclusion metadata we need.
        if (designTime)
        {
            foreach (var definition in SidecarModel.For(table))
                yield return new Annotation(definition.AnnotationName, definition.Serialize());
        }
    }
}

/// <summary>
/// Copies the sidecar annotations onto <c>DropTableOperation</c>, so dropping an entity table (including in a
/// migration's <c>Down</c>) also drops its virtual sidecar tables, which SQLite does not remove on its own.
/// </summary>
sealed class SidecarMigrationsAnnotationProvider(MigrationsAnnotationProviderDependencies dependencies)
    : MigrationsAnnotationProvider(dependencies)
{
    public override IEnumerable<IAnnotation> ForRemove(ITable table)
        => base.ForRemove(table).Concat(
            table.GetAnnotations().Where(a => a.Name.StartsWith(SidecarAnnotationNames.SidecarPrefix, StringComparison.Ordinal)));
}
