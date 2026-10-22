using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using NetTopologySuite.Geometries;

namespace Shiny.EntityFrameworkCore.Sqlite.Mobile.Tests;

public class Place
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Category { get; set; }
    public Point? Location { get; set; }
}

public class Post
{
    public int Id { get; set; }
    public string? Title { get; set; }
    public string? Body { get; set; }
    public float[]? Embedding { get; set; }
}

/// <summary>Successive versions of one schema, as separate context types so each gets its own model.</summary>
public abstract class EvolvingContext(DbContextOptions options) : DbContext(options)
{
    public DbSet<Place> Places => this.Set<Place>();
    public DbSet<Post> Posts => this.Set<Post>();
}

/// <summary>V1: Places(R*Tree on Location), Posts(FTS on Title, vec0 on Embedding).</summary>
public class SchemaV1(DbContextOptions options) : EvolvingContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Place>(e =>
        {
            e.ToTable("Places");
            e.Ignore(x => x.Category);
            e.HasSpatialIndex(x => x.Location);
        });
        modelBuilder.Entity<Post>(e =>
        {
            e.Ignore(x => x.Body);
            e.HasFullTextIndex(x => x.Title);
            e.HasVectorIndex(x => x.Embedding, 2);
        });
    }
}

/// <summary>
/// V2: Places renamed to Locations + a new Category column; Posts gains Body (and the FTS index now covers it), the
/// vector index is dropped, and Title becomes required - which SQLite applies by rebuilding the table.
/// </summary>
public class SchemaV2(DbContextOptions options) : EvolvingContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Place>(e =>
        {
            e.ToTable("Locations");
            e.HasSpatialIndex(x => x.Location);
        });
        modelBuilder.Entity<Post>(e =>
        {
            e.Property(x => x.Title).IsRequired();
            e.HasFullTextIndex(x => new { x.Title, x.Body }, "porter");
        });
    }
}

/// <summary>V1 plus nothing but a new index - must not rebuild the table.</summary>
public class SchemaV1WithPlaceNameIndex(DbContextOptions options) : EvolvingContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Place>(e =>
        {
            e.ToTable("Places");
            e.Ignore(x => x.Category);
            e.HasSpatialIndex(x => x.Location);
            e.HasFullTextIndex(x => x.Name);
        });
        modelBuilder.Entity<Post>(e =>
        {
            e.Ignore(x => x.Body);
            e.HasFullTextIndex(x => x.Title);
            e.HasVectorIndex(x => x.Embedding, 2);
        });
    }
}

/// <summary>Places dropped entirely.</summary>
public class SchemaPostsOnly(DbContextOptions options) : DbContext(options)
{
    public DbSet<Post> Posts => this.Set<Post>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.Entity<Post>(e =>
        {
            e.Ignore(x => x.Body);
            e.HasFullTextIndex(x => x.Title);
            e.HasVectorIndex(x => x.Embedding, 2);
        });
}

public class MigrationTests
{
    [Fact]
    public void EnsureCreated_creates_every_sidecar_and_its_triggers()
    {
        using var db = new TestDatabase();
        using var ctx = db.Mobile();

        var objects = Objects(ctx);
        foreach (var sidecar in new[] { "Regions_Shape_rtree", "Docs_fts", "Docs_Embedding_vec", "Notes_Vector_vec" })
        {
            Assert.Contains($"table:{sidecar}", objects);
            Assert.Contains($"trigger:{sidecar}_ai", objects);
            Assert.Contains($"trigger:{sidecar}_au", objects);
            Assert.Contains($"trigger:{sidecar}_ad", objects);
        }

        // EnsureCreated on an existing database is a no-op (and doesn't fail on the existing sidecars).
        Assert.False(ctx.Database.EnsureCreated());
        Assert.True(ctx.Database.EnsureDeleted());
    }

    [Fact]
    public void GenerateCreateScript_includes_the_sidecars()
    {
        using var db = new TestDatabase();
        using var ctx = db.Mobile(ensureCreated: false);
        var script = ctx.Database.GenerateCreateScript();
        Assert.Contains("CREATE VIRTUAL TABLE IF NOT EXISTS \"Regions_Shape_rtree\" USING rtree(id, minx, maxx, miny, maxy)", script);
        Assert.Contains("USING vec0(embedding float[3] distance_metric=cosine)", script);
        Assert.Contains("USING fts5(\"Title\", \"Body\", content='Docs', content_rowid='Id')", script);
        Assert.Contains("CREATE TRIGGER \"Docs_fts_au\" AFTER UPDATE OF \"Title\", \"Body\", \"Id\" ON \"Docs\"", script);
    }

    [Fact]
    public void Schema_evolution_through_the_migrations_differ()
    {
        using var db = new TestDatabase();

        // ── initial migration (nothing → V1) ──
        using (var v1 = Create(db, o => new SchemaV1(o)))
        {
            Migrate(v1, from: null);
            v1.Places.Add(new Place { Name = "old", Location = Geo.Point(1, 1) });
            v1.Posts.Add(new Post { Title = "hello world", Embedding = [1, 0] });
            v1.SaveChanges();
            Assert.Single(v1.Posts.Where(p => EF.Functions.FullTextMatch(p, "hello")).ToList());
            Assert.Single(v1.Posts.NearestTo(p => p.Embedding, [1, 0], 1).ToList());
        }

        // ── V1 → V2 ──
        using (var v2 = Create(db, o => new SchemaV2(o)))
        {
            using var v1 = Create(db, o => new SchemaV1(o));
            var commands = Migrate(v2, from: DesignModel(v1));
            var sql = string.Join("\n", commands.Select(c => c.CommandText));
            Assert.Contains("ef_temp_Posts", sql); // Title nullability change rebuilt Posts

            var objects = Objects(v2);
            Assert.DoesNotContain("table:Places_Location_rtree", objects);
            Assert.DoesNotContain("trigger:Places_Location_rtree_ai", objects);
            Assert.DoesNotContain("table:Posts_Embedding_vec", objects);
            Assert.Contains("table:Locations_Location_rtree", objects);
            Assert.Contains("trigger:Locations_Location_rtree_ai", objects);
            Assert.Contains("trigger:Posts_fts_ai", objects);
            Assert.Contains("tokenize='porter'", Geo.Strings(v2, "SELECT sql FROM sqlite_master WHERE name = 'Posts_fts'").Single());

            // existing rows were indexed into the new sidecars
            Assert.Equal(1, Geo.Scalar(v2, "SELECT COUNT(*) FROM \"Locations_Location_rtree\""));
            Assert.Single(v2.Posts.Where(p => EF.Functions.FullTextMatch(p, "hello")).ToList());
            Assert.Single(v2.Places.Where(p => p.Location!.Intersects(Geo.Point(1, 1))).ToList());

            // triggers survived the rebuild / rename
            v2.Posts.Add(new Post { Title = "fresh", Body = "running notes" });
            var place = v2.Places.Single();
            place.Location = Geo.Point(50, 50);
            place.Category = "moved";
            v2.SaveChanges();
            Assert.Single(v2.Posts.Where(p => EF.Functions.FullTextMatch(p.Body, "run")).ToList());
            Assert.Single(v2.Places.Where(p => p.Location!.Intersects(Geo.Point(50, 50))).ToList());
            Assert.Empty(v2.Places.Where(p => p.Location!.Intersects(Geo.Point(1, 1))).ToList());
            v2.Database.ExecuteSqlRaw("INSERT INTO \"Posts_fts\" (\"Posts_fts\") VALUES ('integrity-check')");
        }

        // ── V2 → V1 (what a migration's Down does) ──
        using (var v1 = Create(db, o => new SchemaV1(o)))
        {
            using var v2 = Create(db, o => new SchemaV2(o));
            Migrate(v1, from: DesignModel(v2));

            var objects = Objects(v1);
            Assert.Contains("table:Places_Location_rtree", objects);
            Assert.Contains("table:Posts_Embedding_vec", objects);
            Assert.DoesNotContain("table:Locations_Location_rtree", objects);
            Assert.Single(v1.Places.Where(p => p.Location!.Intersects(Geo.Point(50, 50))).ToList());
            Assert.Single(v1.Posts.NearestTo(p => p.Embedding, [1, 0], 1).ToList());
            Assert.Equal(2, v1.Posts.Count(p => EF.Functions.FullTextMatch(p, "hello OR fresh")));
        }
    }

    [Fact]
    public void Adding_only_an_index_does_not_rebuild_the_table()
    {
        using var db = new TestDatabase();
        using var v1 = Create(db, o => new SchemaV1(o));
        Migrate(v1, from: null);
        v1.Places.Add(new Place { Name = "Lighthouse point", Location = Geo.Point(1, 1) });
        v1.SaveChanges();

        using var next = Create(db, o => new SchemaV1WithPlaceNameIndex(o));
        var commands = Migrate(next, from: DesignModel(v1));
        var sql = string.Join("\n", commands.Select(c => c.CommandText));
        Assert.DoesNotContain("ef_temp_", sql);
        Assert.Contains("CREATE VIRTUAL TABLE IF NOT EXISTS \"Places_fts\"", sql);
        Assert.Single(next.Places.Where(p => EF.Functions.FullTextMatch(p, "lighthouse")).ToList());

        // and back again drops just that index
        using var back = Create(db, o => new SchemaV1(o));
        var down = string.Join("\n", Migrate(back, from: DesignModel(next)).Select(c => c.CommandText));
        Assert.Contains("DROP TABLE IF EXISTS \"Places_fts\"", down);
        Assert.DoesNotContain("ef_temp_", down);
        Assert.DoesNotContain("table:Places_fts", Objects(back));
        Assert.Contains("table:Places_Location_rtree", Objects(back));
    }

    [Fact]
    public void Dropping_a_table_drops_its_sidecars()
    {
        using var db = new TestDatabase();
        using var v1 = Create(db, o => new SchemaV1(o));
        Migrate(v1, from: null);

        using var postsOnly = Create(db, o => new SchemaPostsOnly(o));
        Migrate(postsOnly, from: DesignModel(v1));

        var objects = Objects(postsOnly);
        Assert.DoesNotContain("table:Places", objects);
        Assert.DoesNotContain("table:Places_Location_rtree", objects);
        Assert.Contains("table:Posts_fts", objects);
    }

    [Fact]
    public void Database_Migrate_runs_hand_written_and_scaffolded_style_migrations_up_and_down()
    {
        using var db = new TestDatabase();
        var options = new DbContextOptionsBuilder<MigrationsContext>()
            .UseSqlite(db.ConnectionString, o => o.UseShinySpatial().UseShinyFullText())
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        using var ctx = new MigrationsContext(options);

        ctx.Database.Migrate();
        var objects = Objects(ctx);
        Assert.Contains("table:Spots_Location_rtree", objects);
        Assert.Contains("table:Spots_fts", objects);

        ctx.Spots.Add(new Spot { Name = "harbour", Location = Geo.Point(3, 3) });
        ctx.SaveChanges();
        Assert.Single(ctx.Spots.Where(s => s.Location!.Intersects(Geo.Point(3, 3))).ToList());
        Assert.Single(ctx.Spots.Where(s => EF.Functions.FullTextMatch(s, "harbour")).ToList());

        // Down to the first migration removes the FTS index only; down to zero drops everything.
        ctx.GetService<IMigrator>().Migrate("20260101000000_Spots");
        objects = Objects(ctx);
        Assert.DoesNotContain("table:Spots_fts", objects);
        Assert.Contains("table:Spots_Location_rtree", objects);

        ctx.GetService<IMigrator>().Migrate("0");
        objects = Objects(ctx);
        Assert.DoesNotContain("table:Spots", objects);
        Assert.DoesNotContain("table:Spots_Location_rtree", objects);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────────────────

    static TContext Create<TContext>(TestDatabase db, Func<DbContextOptions, TContext> factory) where TContext : DbContext
        => db.Create(factory, o => o.UseShinySpatial().UseShinyVectors().UseShinyFullText(), ensureCreated: false);

    static IModel DesignModel(DbContext context) => context.GetService<IDesignTimeModel>().Model;

    /// <summary>Exactly what the migrator does: diff two models, generate SQL, execute it.</summary>
    static IReadOnlyList<MigrationCommand> Migrate(DbContext target, IModel? from)
    {
        var to = DesignModel(target);
        var operations = target.GetService<IMigrationsModelDiffer>().GetDifferences(from?.GetRelationalModel(), to.GetRelationalModel());
        var commands = target.GetService<IMigrationsSqlGenerator>().Generate(operations, to);
        target.GetService<IMigrationCommandExecutor>().ExecuteNonQuery(commands, target.GetService<IRelationalConnection>());
        return commands;
    }

    static List<string> Objects(DbContext ctx)
        => Geo.Strings(ctx, "SELECT type || ':' || name FROM sqlite_master WHERE type IN ('table', 'trigger')");
}
