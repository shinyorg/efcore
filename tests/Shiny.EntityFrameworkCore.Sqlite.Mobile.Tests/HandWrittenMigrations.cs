using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;

namespace Shiny.EntityFrameworkCore.Sqlite.Mobile.Tests;

public class Spot
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public Point? Location { get; set; }
}

public class MigrationsContext(DbContextOptions<MigrationsContext> options) : DbContext(options)
{
    public DbSet<Spot> Spots => this.Set<Spot>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.Entity<Spot>(e =>
        {
            e.HasSpatialIndex(x => x.Location);
            e.HasFullTextIndex(x => x.Name);
        });
}

// Written exactly as `dotnet ef migrations add` scaffolds them (see ScaffoldTests): the sidecar definitions ride
// along as table annotations, so the migrations replay without needing the model.

[DbContext(typeof(MigrationsContext))]
[Migration("20260101000000_Spots")]
public class SpotsMigration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
                name: "Spots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Location = table.Column<byte[]>(type: "BLOB", nullable: true)
                },
                constraints: table => table.PrimaryKey("PK_Spots", x => x.Id))
            .Annotation("Shiny:Sidecar:Spots_Location_rtree", "{\"kind\":\"RTree\",\"name\":\"Spots_Location_rtree\",\"table\":\"Spots\",\"key\":\"Id\",\"columns\":[\"Location\"]}");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "Spots")
            .Annotation("Shiny:Sidecar:Spots_Location_rtree", "{\"kind\":\"RTree\",\"name\":\"Spots_Location_rtree\",\"table\":\"Spots\",\"key\":\"Id\",\"columns\":[\"Location\"]}");
    }
}

[DbContext(typeof(MigrationsContext))]
[Migration("20260102000000_SpotsFullText")]
public class SpotsFullTextMigration : Migration
{
    const string RTree = "{\"kind\":\"RTree\",\"name\":\"Spots_Location_rtree\",\"table\":\"Spots\",\"key\":\"Id\",\"columns\":[\"Location\"]}";
    const string Fts = "{\"kind\":\"FullText\",\"name\":\"Spots_fts\",\"table\":\"Spots\",\"key\":\"Id\",\"columns\":[\"Name\"]}";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterTable(name: "Spots")
            .Annotation("Shiny:Sidecar:Spots_Location_rtree", RTree)
            .Annotation("Shiny:Sidecar:Spots_fts", Fts)
            .OldAnnotation("Shiny:Sidecar:Spots_Location_rtree", RTree);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterTable(name: "Spots")
            .Annotation("Shiny:Sidecar:Spots_Location_rtree", RTree)
            .OldAnnotation("Shiny:Sidecar:Spots_Location_rtree", RTree)
            .OldAnnotation("Shiny:Sidecar:Spots_fts", Fts);
    }
}
