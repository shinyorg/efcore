using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Migrations.Design;
using Microsoft.EntityFrameworkCore.Sqlite.Design.Internal;
using Microsoft.Extensions.DependencyInjection;

namespace Shiny.EntityFrameworkCore.Sqlite.Mobile.Tests;

/// <summary>What <c>dotnet ef migrations add</c> would write for the sidecar indexes.</summary>
public class ScaffoldTests
{
    [Fact]
    public void Scaffolded_migration_carries_the_sidecar_annotations()
    {
        using var db = new TestDatabase();
        using var ctx = db.Mobile(ensureCreated: false);

        var services = new ServiceCollection()
            .AddEntityFrameworkDesignTimeServices()
            .AddDbContextDesignTimeServices(ctx);
        new SqliteDesignTimeServices().ConfigureDesignTimeServices(services);
        using var provider = services.BuildServiceProvider();

        var migration = provider.GetRequiredService<IMigrationsScaffolder>().ScaffoldMigration("Initial", "Scaffolded");
        TestContext.Current.TestOutputHelper!.WriteLine(migration.MigrationCode);

        // Up: CreateTable(...).Annotation("Shiny:Sidecar:<name>", "<definition json>") for each sidecar
        Assert.Contains(".Annotation(\"Shiny:Sidecar:Regions_Shape_rtree\"", migration.MigrationCode);
        Assert.Contains(".Annotation(\"Shiny:Sidecar:Docs_fts\"", migration.MigrationCode);
        Assert.Contains(".Annotation(\"Shiny:Sidecar:Docs_Embedding_vec\"", migration.MigrationCode);
        Assert.Contains(".Annotation(\"Shiny:Sidecar:Notes_Vector_vec\"", migration.MigrationCode);

        // Down: DropTable carries them too, so the virtual tables are dropped with their entity table.
        Assert.Equal(2, Occurrences(migration.MigrationCode, "\"Shiny:Sidecar:Docs_fts\""));

        // The sidecar tables themselves are excluded from migrations (EF never creates/drops them).
        Assert.DoesNotContain("name: \"Docs_fts\"", migration.MigrationCode);

        // The snapshot keeps the model-level configuration.
        Assert.Contains("Shiny:SpatialIndex", migration.SnapshotCode);
        Assert.Contains("Shiny:FullTextIndex", migration.SnapshotCode);
        Assert.Contains("Shiny:VectorIndexDimensions", migration.SnapshotCode);
    }

    static int Occurrences(string text, string value)
    {
        var count = 0;
        var index = text.IndexOf(value, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal);
        }
        return count;
    }
}
