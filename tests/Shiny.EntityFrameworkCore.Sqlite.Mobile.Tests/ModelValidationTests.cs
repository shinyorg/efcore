using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using NetTopologySuite.Geometries;

namespace Shiny.EntityFrameworkCore.Sqlite.Mobile.Tests;

public class ModelValidationTests
{
    public class GuidPlace
    {
        public Guid Id { get; set; }
        public Point? Location { get; set; }
    }

    public class StringKeyDoc
    {
        public string Id { get; set; } = "";
        public string? Text { get; set; }
        public float[]? Embedding { get; set; }
    }

    public class CompositeDoc
    {
        public int A { get; set; }
        public int B { get; set; }
        public string? Text { get; set; }
    }

    public class Keyless
    {
        public string? Text { get; set; }
    }

    public class Owner
    {
        public int Id { get; set; }
        public Address Address { get; set; } = new();
    }

    public class Address
    {
        public string? Street { get; set; }
        public Point? Location { get; set; }
    }

    public class Shape
    {
        public int Id { get; set; }
        public Geometry? Outline { get; set; }
    }

    public class Square : Shape
    {
        public double Side { get; set; }
    }

    public class Numbered
    {
        public int Id { get; set; }
        public string? Text { get; set; }
        public int Count { get; set; }
        public float[]? Embedding { get; set; }
    }

    public class ConfigurableContext(DbContextOptions options, Action<ModelBuilder> configure) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => configure(modelBuilder);
    }

    [Fact]
    public void Non_integer_key_is_rejected()
    {
        var error = Build(m => m.Entity<GuidPlace>().HasSpatialIndex(x => x.Location), o => o.UseShinySpatial());
        Assert.Contains("single integer primary key", error.Message);
        Assert.Contains("Guid Id", error.Message);
        Assert.Contains("VACUUM", error.Message);

        var vector = Build(m => m.Entity<StringKeyDoc>().HasVectorIndex(x => x.Embedding, 3), o => o.UseShinyVectors());
        Assert.Contains("single integer primary key", vector.Message);

        var fts = Build(m => m.Entity<StringKeyDoc>().HasFullTextIndex(x => x.Text), o => o.UseShinyFullText());
        Assert.Contains("String Id", fts.Message);
    }

    [Fact]
    public void Composite_key_is_rejected()
    {
        var error = Build(
            m => m.Entity<CompositeDoc>(e =>
            {
                e.HasKey(x => new { x.A, x.B });
                e.HasFullTextIndex(x => x.Text);
            }),
            o => o.UseShinyFullText());
        Assert.Contains("Int32 A, Int32 B", error.Message);
    }

    [Fact]
    public void Integer_key_stored_in_a_non_INTEGER_column_is_rejected()
    {
        var error = Build(
            m => m.Entity<Numbered>(e =>
            {
                e.Property(x => x.Id).HasColumnType("BIGINT");
                e.HasFullTextIndex(x => x.Text);
            }),
            o => o.UseShinyFullText());
        Assert.Contains("INTEGER column", error.Message);
    }

    [Fact]
    public void Keyless_owned_view_and_TPC_entities_are_rejected()
    {
        var keyless = Build(m => m.Entity<Keyless>(e => { e.HasNoKey(); e.HasFullTextIndex(x => x.Text); }), o => o.UseShinyFullText());
        Assert.Contains("no primary key", keyless.Message);

        var owned = Build(
            m => m.Entity<Owner>().OwnsOne(x => x.Address, a => a.Property(x => x.Location).HasAnnotation("Shiny:SpatialIndex", true)),
            o => o.UseShinySpatial());
        Assert.Contains("owned entity type", owned.Message);

        var view = Build(m => m.Entity<Numbered>(e => { e.ToView("NumberedView"); e.HasFullTextIndex(x => x.Text); }), o => o.UseShinyFullText());
        Assert.Contains("mapped to a table", view.Message);

        var tpc = Build(
            m =>
            {
                m.Entity<Shape>(e =>
                {
                    e.UseTpcMappingStrategy();
                    e.HasSpatialIndex(x => x.Outline);
                });
                m.Entity<Square>();
            },
            o => o.UseShinySpatial());
        Assert.Contains("TPC", tpc.Message);
    }

    [Fact]
    public void Wrong_property_types_are_rejected()
    {
        var fts = Build(m => m.Entity<Numbered>().HasAnnotation("Shiny:FullTextIndex", "Text,Count"), o => o.UseShinyFullText());
        Assert.Contains("only string properties", fts.Message);

        var missing = Build(m => m.Entity<Numbered>().HasAnnotation("Shiny:FullTextIndex", "Nope"), o => o.UseShinyFullText());
        Assert.Contains("not a mapped property", missing.Message);

        var empty = Build(m => m.Entity<Numbered>().HasAnnotation("Shiny:FullTextIndex", ","), o => o.UseShinyFullText());
        Assert.Contains("has no properties", empty.Message);

        var vector = Build(m => m.Entity<Numbered>().Property(x => x.Text).HasAnnotation("Shiny:VectorIndexDimensions", 3), o => o.UseShinyVectors());
        Assert.Contains("is not float[]", vector.Message);

        var dims = Build(m => m.Entity<Numbered>().Property(x => x.Embedding).HasAnnotation("Shiny:VectorIndexDimensions", 0), o => o.UseShinyVectors());
        Assert.Contains("dimensions must be positive", dims.Message);

        var spatial = Build(
            m => m.Entity<Numbered>(e => e.Property(x => x.Text).HasAnnotation("Shiny:SpatialIndex", true)),
            o => o.UseShinySpatial());
        Assert.Contains("not a NetTopologySuite Geometry", spatial.Message);
    }

    [Fact]
    public void Index_without_its_feature_enabled_is_rejected()
    {
        var vector = Build(
            m => m.Entity<Numbered>(e => e.HasVectorIndex(x => x.Embedding, 3)),
            o => o.UseShinySpatial());
        Assert.Contains("UseShinyVectors()", vector.Message);

        var fts = Build(m => m.Entity<Numbered>(e => e.HasFullTextIndex(x => x.Text)), o => o.UseShinySpatial());
        Assert.Contains("UseShinyFullText()", fts.Message);

        var spatial = Build(
            m => m.Entity<Numbered>(e => e.Property(x => x.Count).HasAnnotation("Shiny:SpatialIndex", true)),
            o => o.UseShinyFullText());
        Assert.Contains("UseShinySpatial()", spatial.Message);
    }

    [Fact]
    public void Fluent_api_guards()
    {
        var builder = new ModelBuilder();
        var entity = builder.Entity<Numbered>();
        Assert.Throws<ArgumentOutOfRangeException>(() => entity.HasVectorIndex(x => x.Embedding, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => entity.HasVectorIndex(x => x.Embedding, 3, (VectorDistanceMetric)42));
        Assert.Throws<ArgumentException>(() => entity.HasFullTextIndex(x => new { }));
        Assert.Throws<ArgumentNullException>(() => entity.HasSpatialIndex<Numbered, Point>(null!));
        Assert.Throws<ArgumentNullException>(() => entity.HasFullTextIndex(null!));

        entity.HasFullTextIndex(x => x.Text, "porter");
        Assert.Equal("porter", entity.Metadata.FindAnnotation("Shiny:FullTextTokenizer")!.Value);
        entity.HasFullTextIndex(x => x.Text);
        Assert.Null(entity.Metadata.FindAnnotation("Shiny:FullTextTokenizer"));
    }

    [Fact]
    public void Valid_inheritance_shares_the_root_table()
    {
        using var db = new TestDatabase();
        var options = new DbContextOptionsBuilder<ConfigurableContext>()
            .UseSqlite(db.ConnectionString, o => o.UseShinySpatial())
            .EnableServiceProviderCaching(false)
            .Options;
        using var ctx = new ConfigurableContext(options, m =>
        {
            m.Entity<Shape>().HasSpatialIndex(x => x.Outline);
            m.Entity<Square>();
        });
        ctx.Database.EnsureCreated();
        ctx.Add(new Square { Side = 2, Outline = Geo.Wkt("POLYGON ((0 0, 2 0, 2 2, 0 2, 0 0))") });
        ctx.Add(new Shape { Outline = Geo.Point(9, 9) });
        ctx.SaveChanges();

        var hit = Geo.Point(1, 1);
        var squares = ctx.Set<Square>().Where(s => s.Outline!.Contains(hit)).ToList();
        Assert.Single(squares);
        Assert.Contains("\"Shape_Outline_rtree\"", ctx.Set<Square>().Where(s => s.Outline!.Contains(hit)).ToQueryString());
        Assert.Equal(2, Geo.Scalar(ctx, "SELECT COUNT(*) FROM \"Shape_Outline_rtree\""));
    }

    static InvalidOperationException Build(Action<ModelBuilder> configure, Action<SqliteDbContextOptionsBuilder> features)
    {
        var options = new DbContextOptionsBuilder<ConfigurableContext>()
            .UseSqlite("Data Source=:memory:", features)
            .EnableServiceProviderCaching(false)
            .Options;
        using var ctx = new ConfigurableContext(options, configure);
        return Assert.Throws<InvalidOperationException>(() => ctx.Model);
    }
}
