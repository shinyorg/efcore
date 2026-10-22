using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Shiny.EntityFrameworkCore.Sqlite.Mobile.Sidecars;

/// <summary>
/// Extends EF's SQLite migrations SQL generator with the sidecar lifecycle. It is the single place sidecar DDL is
/// produced, for both <c>EnsureCreated</c> (which runs a model-to-nothing diff through this generator) and
/// migrations (whose operations carry the <c>Shiny:Sidecar:*</c> table annotations in their generated code):
/// <list type="bullet">
///   <item>Removed / changed sidecars are dropped <b>before</b> EF's own operations, so no trigger references a
///   column that EF is about to rename or drop.</item>
///   <item>New / changed sidecars are created, populated from existing rows and given their triggers <b>after</b>
///   EF's operations, so the columns they read exist.</item>
///   <item>Any other operation on a table that has sidecars re-creates that table's triggers afterwards: SQLite
///   applies many column changes by rebuilding the table, and dropping the old table drops its triggers. Sidecar rows
///   survive a rebuild because they are keyed by the INTEGER PRIMARY KEY, which the rebuild copies.</item>
///   <item>An <c>AlterTableOperation</c> that only changes sidecar annotations is consumed here, so adding an index
///   does not trigger an unnecessary SQLite table rebuild.</item>
/// </list>
/// </summary>
sealed class SidecarMigrationsSqlGenerator(MigrationsSqlGeneratorDependencies dependencies, IRelationalAnnotationProvider annotations)
    : SqliteMigrationsSqlGenerator(dependencies, annotations)
{
    public override IReadOnlyList<MigrationCommand> Generate(
        IReadOnlyList<MigrationOperation> operations,
        IModel? model = null,
        MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default)
    {
        var before = new List<string>();
        var after = new List<string>();
        var passThrough = new List<MigrationOperation>(operations.Count);
        var installed = new HashSet<string>(StringComparer.Ordinal);
        var dropped = new HashSet<string>(StringComparer.Ordinal);
        var touched = new List<string>();

        foreach (var operation in operations)
        {
            switch (operation)
            {
                case CreateTableOperation create:
                    foreach (var definition in SidecarDefinition.From(create.GetAnnotations()))
                    {
                        after.AddRange(SidecarSql.Install(definition));
                        installed.Add(definition.Name);
                    }
                    passThrough.Add(create);
                    break;

                case DropTableOperation drop:
                    foreach (var definition in SidecarDefinition.From(drop.GetAnnotations()))
                        before.AddRange(SidecarSql.Drop(definition));
                    dropped.Add(drop.Name);
                    passThrough.Add(drop);
                    break;

                case AlterTableOperation alter:
                    var oldDefinitions = SidecarDefinition.From(alter.OldTable.GetAnnotations());
                    var newDefinitions = SidecarDefinition.From(alter.GetAnnotations());

                    foreach (var definition in oldDefinitions.Where(o => !newDefinitions.Any(n => Same(o, n))))
                        before.AddRange(SidecarSql.Drop(definition));

                    foreach (var definition in newDefinitions.Where(n => !oldDefinitions.Any(o => Same(o, n))))
                    {
                        after.AddRange(SidecarSql.Install(definition));
                        installed.Add(definition.Name);
                    }

                    if (!OnlySidecarChanges(alter))
                    {
                        passThrough.Add(alter);
                        touched.Add(alter.Name);
                    }
                    break;

                case RenameTableOperation rename:
                    touched.Add(rename.NewName ?? rename.Name);
                    passThrough.Add(rename);
                    break;

                case ITableMigrationOperation tableOperation:
                    touched.Add(tableOperation.Table);
                    passThrough.Add(operation);
                    break;

                default:
                    passThrough.Add(operation);
                    break;
            }
        }

        if (model != null)
        {
            var relationalModel = model.GetRelationalModel();
            foreach (var tableName in touched.Distinct(StringComparer.Ordinal).Where(t => !dropped.Contains(t)))
            {
                var table = relationalModel.FindTable(tableName, null);
                var definitions = table == null ? [] : SidecarDefinition.From(table.GetAnnotations());
                foreach (var definition in definitions.Where(d => !installed.Contains(d.Name)))
                    after.AddRange(SidecarSql.CreateTriggers(definition));
            }
        }

        var commands = new List<MigrationCommand>();
        commands.AddRange(this.Build(before));
        commands.AddRange(base.Generate(passThrough, model, options));
        commands.AddRange(this.Build(after));
        return commands;
    }

    IReadOnlyList<MigrationCommand> Build(IEnumerable<string> statements)
    {
        var builder = new MigrationCommandListBuilder(this.Dependencies);
        foreach (var sql in statements)
        {
            builder
                .Append(sql)
                .AppendLine(this.Dependencies.SqlGenerationHelper.StatementTerminator)
                .EndCommand();
        }
        return builder.GetCommandList();
    }

    static bool Same(SidecarDefinition a, SidecarDefinition b)
        => a.Name == b.Name && a.Serialize() == b.Serialize();

    static bool OnlySidecarChanges(AlterTableOperation alter)
    {
        if (!string.Equals(alter.Comment, alter.OldTable.Comment, StringComparison.Ordinal))
            return false;

        var current = Others(alter.GetAnnotations());
        var old = Others(alter.OldTable.GetAnnotations());
        return current.Count == old.Count && current.All(kv => old.TryGetValue(kv.Key, out var v) && Equals(v, kv.Value));
    }

    static Dictionary<string, object?> Others(IEnumerable<IAnnotation> annotations)
        => annotations
            .Where(a => !a.Name.StartsWith(SidecarAnnotationNames.SidecarPrefix, StringComparison.Ordinal))
            .ToDictionary(a => a.Name, a => a.Value, StringComparer.Ordinal);
}
