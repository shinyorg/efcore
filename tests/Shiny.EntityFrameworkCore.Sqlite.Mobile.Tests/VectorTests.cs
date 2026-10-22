using Microsoft.EntityFrameworkCore;

namespace Shiny.EntityFrameworkCore.Sqlite.Mobile.Tests;

public class VectorTests
{
    [Fact]
    public void Vectors_round_trip_as_float32_blobs()
    {
        using var db = new TestDatabase();
        using (var ctx = db.Mobile())
        {
            ctx.Docs.Add(new Doc { Title = "a", Embedding = [0.25f, -1.5f, 3f] });
            ctx.Docs.Add(new Doc { Title = "none" });
            ctx.Notes.Add(new Note { Name = "n", Vector = new float[] { 1f, 2f } });
            ctx.Samples.Add(new Sample { Label = "s", Raw = [1, 2, 3], Json = [4, 5] });
            ctx.SaveChanges();

            Assert.Equal(12, Geo.Scalar(ctx, "SELECT length(\"Embedding\") FROM \"Docs\" WHERE \"Title\" = 'a'"));
            Assert.Equal(["blob"], Geo.Strings(ctx, "SELECT typeof(\"Embedding\") FROM \"Docs\" WHERE \"Title\" = 'a'"));

            // HasColumnType("TEXT") opts a float[] back out to EF's JSON primitive collection.
            Assert.Equal(["[4,5]"], Geo.Strings(ctx, "SELECT \"Json\" FROM \"Samples\""));
        }

        using var read = db.Mobile(ensureCreated: false);
        Assert.Equal([0.25f, -1.5f, 3f], read.Docs.Single(d => d.Title == "a").Embedding!);
        Assert.Null(read.Docs.Single(d => d.Title == "none").Embedding);
        Assert.Equal([1f, 2f], read.Notes.Single().Vector.ToArray());
        var sample = read.Samples.Single();
        Assert.Equal([1f, 2f, 3f], sample.Raw!);
        Assert.Equal([4f, 5f], sample.Json!);
    }

    [Fact]
    public void Vector_mutation_is_detected_by_change_tracking()
    {
        using var db = new TestDatabase();
        using var ctx = db.Mobile();
        var doc = new Doc { Title = "a", Embedding = [1, 0, 0] };
        ctx.Docs.Add(doc);
        ctx.SaveChanges();

        doc.Embedding[1] = 5;
        ctx.SaveChanges();

        ctx.ChangeTracker.Clear();
        Assert.Equal([1f, 5f, 0f], ctx.Docs.Single().Embedding!);
    }

    [Theory]
    [InlineData(VectorDistanceMetric.Cosine)]
    [InlineData(VectorDistanceMetric.Euclidean)]
    [InlineData(VectorDistanceMetric.Manhattan)]
    public void VectorDistance_matches_brute_force_without_an_index(VectorDistanceMetric metric)
    {
        using var db = new TestDatabase();
        using var ctx = db.Mobile();
        var random = new Random(7);
        for (var i = 0; i < 40; i++)
            ctx.Samples.Add(new Sample { Label = "s" + i, Raw = [(float)random.NextDouble(), (float)random.NextDouble(), (float)random.NextDouble()] });
        ctx.SaveChanges();
        var all = ctx.Samples.AsNoTracking().ToList();
        float[] query = [0.2f, 0.9f, 0.4f];

        var expected = all.OrderBy(s => EF.Functions.VectorDistance(s.Raw, query, metric)).Select(s => s.Id).Take(10).ToList();
        var actual = ctx.Samples.OrderBy(s => EF.Functions.VectorDistance(s.Raw, query, metric)).Select(s => s.Id).Take(10).ToList();
        Assert.Equal(expected, actual);

        var distances = ctx.Samples.Select(s => new { s.Id, D = EF.Functions.VectorDistance(s.Raw, query, metric) }).ToList();
        foreach (var d in distances)
            Assert.Equal(EF.Functions.VectorDistance(all.Single(s => s.Id == d.Id).Raw, query, metric), d.D, 5);

        var threshold = distances.Select(d => d.D).Order().ElementAt(5);
        var within = ctx.Samples.Where(s => EF.Functions.VectorDistance(s.Raw, query, metric) <= threshold).Count();
        Assert.Equal(6, within);
    }

    [Fact]
    public void VectorDistance_accepts_a_parameterised_metric()
    {
        using var db = new TestDatabase();
        using var ctx = db.Mobile();
        ctx.Samples.Add(new Sample { Label = "a", Raw = [3, 4, 0] });
        ctx.SaveChanges();
        float[] origin = [0, 0, 0];

        foreach (var metric in Enum.GetValues<VectorDistanceMetric>().Where(m => m != VectorDistanceMetric.Cosine))
        {
            var query = ctx.Samples.Select(s => EF.Functions.VectorDistance(s.Raw, origin, metric));
            Assert.Contains("CASE", query.ToQueryString());
            Assert.Equal(metric == VectorDistanceMetric.Euclidean ? 5 : 7, query.Single(), 5);
        }

        Assert.Equal(5, ctx.Samples.Select(s => EF.Functions.VectorDistance(s.Raw, origin, VectorDistanceMetric.Euclidean)).Single(), 5);
        Assert.DoesNotContain("CASE", ctx.Samples.Select(s => EF.Functions.VectorDistance(s.Raw, origin, VectorDistanceMetric.Euclidean)).ToQueryString());
    }

    [Fact]
    public void Client_side_VectorDistance_validates_input()
    {
        Assert.Equal(0, EF.Functions.VectorDistance(new float[] { 1, 0 }, new float[] { 2, 0 }, VectorDistanceMetric.Cosine), 9);
        Assert.Equal(2, EF.Functions.VectorDistance(new ReadOnlyMemory<float>([1, 0]), new ReadOnlyMemory<float>([-1, 0]), VectorDistanceMetric.Cosine), 9);
        Assert.Throws<ArgumentException>(() => EF.Functions.VectorDistance(new float[] { 1 }, new float[] { 1, 2 }, VectorDistanceMetric.Euclidean));
        Assert.Throws<ArgumentNullException>(() => EF.Functions.VectorDistance(null, new float[] { 1 }, VectorDistanceMetric.Euclidean));
    }

    [Fact]
    public void NearestTo_returns_the_k_nearest_by_the_index_metric()
    {
        using var db = new TestDatabase();
        using var ctx = db.Mobile();
        var random = new Random(11);
        for (var i = 0; i < 60; i++)
        {
            ctx.Docs.Add(new Doc
            {
                Title = "doc" + i,
                Tag = i % 3 == 0 ? "a" : "b",
                Embedding = [(float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f]
            });
        }
        ctx.SaveChanges();
        var all = ctx.Docs.AsNoTracking().ToList();
        float[] query = [0.3f, -0.2f, 0.1f];

        var expected = all
            .Select(d => new { d.Id, D = EF.Functions.VectorDistance(d.Embedding, query, VectorDistanceMetric.Cosine) })
            .OrderBy(x => x.D)
            .Take(5)
            .ToList();

        var knn = ctx.Docs.NearestTo(d => d.Embedding, query, 5);
        var actual = knn.ToList();
        Assert.Equal(expected.Select(e => e.Id), actual.Select(a => a.Entity.Id));
        for (var i = 0; i < expected.Count; i++)
            Assert.Equal(expected[i].D, actual[i].Distance, 4);

        var sql = knn.ToQueryString();
        Assert.Contains("\"Docs_Embedding_vec\"", sql);
        Assert.Contains("match(@Query", sql);
        Assert.Contains("\"k\" = @K", sql);
        Assert.Contains("vec0", Geo.Strings(ctx, "SELECT sql FROM sqlite_master WHERE name = 'Docs_Embedding_vec'").Single());

        // entities are tracked
        Assert.Same(actual[0].Entity, ctx.Docs.Local.Single(d => d.Id == actual[0].Entity.Id));
    }

    [Fact]
    public void NearestTo_composes_with_filters_projection_and_paging()
    {
        using var db = new TestDatabase();
        using var ctx = db.Mobile();
        for (var i = 0; i < 10; i++)
            ctx.Docs.Add(new Doc { Title = "d" + i, Tag = i % 2 == 0 ? "even" : "odd", Embedding = [1, i, 0] });
        ctx.SaveChanges();
        ctx.ChangeTracker.Clear();
        float[] query = [1, 0, 0];

        // pre-composed source filter is applied to the k nearest (post-filter)
        var evens = ctx.Docs.Where(d => d.Tag == "even").NearestTo(d => d.Embedding, query, 4).Select(m => m.Entity.Title).ToList();
        Assert.Equal(["d0", "d2"], evens);

        var page = ctx.Docs.AsNoTracking()
            .NearestTo(d => d.Embedding, query, 6)
            .Where(m => m.Distance > 0)
            .Skip(1)
            .Take(2)
            .Select(m => new { m.Entity.Title, m.Distance })
            .ToList();
        Assert.Equal(["d2", "d3"], page.Select(p => p.Title));
        Assert.True(page[0].Distance < page[1].Distance);
        Assert.Empty(ctx.ChangeTracker.Entries());
    }

    [Fact]
    public async Task NearestTo_over_ReadOnlyMemory_with_Euclidean_index()
    {
        using var db = new TestDatabase();
        using var ctx = db.Mobile();
        ctx.Notes.Add(new Note { Name = "origin", Vector = new float[] { 0, 0 } });
        ctx.Notes.Add(new Note { Name = "near", Vector = new float[] { 1, 1 } });
        ctx.Notes.Add(new Note { Name = "far", Vector = new float[] { 10, 10 } });
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await ctx.Notes.NearestTo(n => n.Vector, new ReadOnlyMemory<float>([0.9f, 0.9f]), 2).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["near", "origin"], result.Select(r => r.Entity.Name));
        Assert.Equal(Math.Sqrt(0.02), result[0].Distance, 4);
        Assert.Contains("distance_metric=l2", Geo.Strings(ctx, "SELECT sql FROM sqlite_master WHERE name = 'Notes_Vector_vec'").Single());
    }

    [Fact]
    public void Vector_index_stays_in_sync_through_every_write_path()
    {
        using var db = new TestDatabase();
        using var ctx = db.Mobile();
        var doc = new Doc { Title = "a", Embedding = [1, 0, 0] };
        ctx.Docs.Add(doc);
        ctx.Docs.Add(new Doc { Title = "no-vector" });
        ctx.SaveChanges();
        Assert.Equal(1, Rows(ctx));
        Assert.Equal("a", Nearest(ctx, [1, 0, 0]));

        // update = delete + insert (vec0 has no INSERT OR REPLACE); repeat to prove it's not a one-off
        doc.Embedding = [0, 1, 0];
        ctx.SaveChanges();
        doc.Embedding = [0, 0, 1];
        ctx.SaveChanges();
        Assert.Equal(1, Rows(ctx));
        Assert.Equal(0, ctx.Docs.NearestTo(d => d.Embedding, [0, 0, 1], 1).Select(m => m.Distance).Single(), 5);

        // null removes, ExecuteUpdate restores
        doc.Embedding = null;
        ctx.SaveChanges();
        Assert.Equal(0, Rows(ctx));
        float[] restored = [1, 1, 0];
        ctx.Docs.Where(d => d.Id == doc.Id).ExecuteUpdate(s => s.SetProperty(d => d.Embedding, restored));
        Assert.Equal(1, Rows(ctx));

        ctx.Docs.Where(d => d.Title == "no-vector").ExecuteUpdate(s => s.SetProperty(d => d.Embedding, restored));
        Assert.Equal(2, Rows(ctx));

        ctx.Docs.Where(d => d.Title == "no-vector").ExecuteDelete();
        Assert.Equal(1, Rows(ctx));

        ctx.ChangeTracker.Clear();
        ctx.Docs.Remove(ctx.Docs.Single());
        ctx.SaveChanges();
        Assert.Equal(0, Rows(ctx));
    }

    [Fact]
    public void Empty_ReadOnlyMemory_is_stored_but_not_indexed()
    {
        using var db = new TestDatabase();
        using var ctx = db.Mobile();
        var note = new Note { Name = "no vector yet" };
        ctx.Notes.Add(note);
        ctx.SaveChanges();
        Assert.Equal(0, Geo.Scalar(ctx, "SELECT COUNT(*) FROM \"Notes_Vector_vec\""));

        note.Vector = new float[] { 1, 2 };
        ctx.SaveChanges();
        Assert.Equal(1, Geo.Scalar(ctx, "SELECT COUNT(*) FROM \"Notes_Vector_vec\""));

        note.Vector = default;
        ctx.SaveChanges();
        Assert.Equal(0, Geo.Scalar(ctx, "SELECT COUNT(*) FROM \"Notes_Vector_vec\""));
        ctx.ChangeTracker.Clear();
        Assert.True(ctx.Notes.Single().Vector.IsEmpty);
    }

    [Fact]
    public void Wrong_dimensions_are_rejected_by_the_index()
    {
        using var db = new TestDatabase();
        using var ctx = db.Mobile();
        ctx.Docs.Add(new Doc { Title = "bad", Embedding = [1, 2, 3, 4] });
        var error = Assert.Throws<DbUpdateException>(() => ctx.SaveChanges());
        Assert.Contains("imension", error.InnerException!.Message);

        ctx.ChangeTracker.Clear();
        Assert.Equal(0, ctx.Docs.Count());
    }

    [Fact]
    public void NearestTo_guards()
    {
        using var db = new TestDatabase();
        using var ctx = db.Mobile();

        var noIndex = Assert.Throws<InvalidOperationException>(() => ctx.Samples.NearestTo(s => s.Raw, [1, 2, 3], 3));
        Assert.Contains("no vector index", noIndex.Message);

        Assert.Throws<ArgumentOutOfRangeException>(() => ctx.Docs.NearestTo(d => d.Embedding, [1, 2, 3], 0));
        Assert.Throws<ArgumentNullException>(() => ctx.Docs.NearestTo(d => d.Embedding, null!, 1));
        Assert.Throws<InvalidOperationException>(() => new List<Doc>().AsQueryable().NearestTo(d => d.Embedding, [1, 2, 3], 1));
    }

    [Fact]
    public void Other_float_arrays_still_work_as_query_collections()
    {
        using var db = new TestDatabase();
        using var ctx = db.Mobile();
        ctx.Samples.Add(new Sample { Label = "a", Weight = 1 });
        ctx.Samples.Add(new Sample { Label = "b", Weight = 2 });
        ctx.Samples.Add(new Sample { Label = "c", Weight = 3 });
        ctx.SaveChanges();

        float[] wanted = [1, 3];
        Assert.Equal(["a", "c"], ctx.Samples.Where(s => wanted.Contains(s.Weight)).Select(s => s.Label).OrderBy(l => l).ToList());
    }

    [Fact]
    public void Manhattan_vector_index()
    {
        using var db = new TestDatabase();
        using var ctx = db.Create(o => new ManhattanContext(o), o => o.UseShinyVectors());
        ctx.Samples.Add(new Sample { Label = "a", Raw = [0, 0, 0] });
        ctx.Samples.Add(new Sample { Label = "b", Raw = [1, 1, 1] });
        ctx.SaveChanges();

        Assert.Contains("distance_metric=l1", Geo.Strings(ctx, "SELECT sql FROM sqlite_master WHERE name = 'Samples_Raw_vec'").Single());
        var nearest = ctx.Samples.NearestTo(s => s.Raw, [1, 1, 2], 1).Single();
        Assert.Equal("b", nearest.Entity.Label);
        Assert.Equal(1, nearest.Distance, 5);
    }

    [Fact]
    public void Compiled_queries_use_the_index_rewrites()
    {
        using var db = new TestDatabase();
        using var ctx = db.Mobile();
        ctx.Docs.Add(new Doc { Title = "compiled query", Body = "fast" });
        ctx.Regions.Add(new Region { Name = "r", Shape = Geo.Point(2, 2) });
        ctx.SaveChanges();

        var search = EF.CompileQuery((MobileContext c, string q) => c.Docs.Where(d => EF.Functions.FullTextMatch(d, q)));
        Assert.Single(search(ctx, "compiled").ToList());
        Assert.Empty(search(ctx, "slow").ToList());

        var within = EF.CompileQuery((MobileContext c, NetTopologySuite.Geometries.Geometry area) => c.Regions.Count(r => r.Shape!.Intersects(area)));
        Assert.Equal(1, within(ctx, Geo.Wkt("POLYGON ((0 0, 3 0, 3 3, 0 3, 0 0))")));
        Assert.Equal(0, within(ctx, Geo.Wkt("POLYGON ((5 5, 6 5, 6 6, 5 6, 5 5))")));
    }

    static long Rows(DbContext ctx) => Geo.Scalar(ctx, "SELECT COUNT(*) FROM \"Docs_Embedding_vec\"");

    static string Nearest(MobileContext ctx, float[] query)
        => ctx.Docs.NearestTo(d => d.Embedding, query, 1).Select(m => m.Entity.Title).Single();
}

public class ManhattanContext(DbContextOptions options) : DbContext(options)
{
    public DbSet<Sample> Samples => this.Set<Sample>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.Entity<Sample>(e =>
        {
            e.Ignore(x => x.Json);
            e.HasVectorIndex(x => x.Raw, 3, VectorDistanceMetric.Manhattan);
        });
}
