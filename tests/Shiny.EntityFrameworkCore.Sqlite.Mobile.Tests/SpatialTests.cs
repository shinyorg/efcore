using System.Linq.Expressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;

namespace Shiny.EntityFrameworkCore.Sqlite.Mobile.Tests;

public class SpatialTests
{
    static readonly string[] shapes =
    [
        "POINT (0 0)",
        "POINT (5 5)",
        "POINT (10 10)",
        "POINT (2 8)",
        "POINT (-3 -3)",
        "POINT (4 2)",
        "LINESTRING (0 0, 10 10)",
        "LINESTRING (0 10, 10 0)",
        "LINESTRING (20 20, 30 30)",
        "LINESTRING (4 0, 4 4)",
        "POLYGON ((0 0, 4 0, 4 4, 0 4, 0 0))",
        "POLYGON ((3 3, 8 3, 8 8, 3 8, 3 3))",
        "POLYGON ((20 0, 25 0, 25 5, 20 5, 20 0))",
        "POLYGON ((0 0, 10 0, 10 10, 0 10, 0 0), (4 4, 6 4, 6 6, 4 6, 4 4))",
        "POLYGON ((1 1, 6 1, 6 6, 1 6, 1 1))",
        "MULTIPOINT ((1 1), (9 9))",
        "MULTIPOLYGON (((50 50, 52 50, 52 52, 50 52, 50 50)), ((2 2, 3 2, 3 3, 2 3, 2 2)))",
        "GEOMETRYCOLLECTION (POINT (7 1), LINESTRING (6 0, 9 3))"
    ];

    static readonly string[] queries =
    [
        "POLYGON ((1 1, 6 1, 6 6, 1 6, 1 1))",
        "POINT (5 5)",
        "POINT (4 2)",
        "LINESTRING (-1 5, 11 5)",
        "POLYGON ((0 0, 4 0, 4 4, 0 4, 0 0))",
        "POLYGON ((100 100, 101 100, 101 101, 100 101, 100 100))"
    ];

    public static TheoryData<string> Predicates =>
    [
        nameof(Geometry.Intersects),
        nameof(Geometry.Contains),
        nameof(Geometry.Within),
        nameof(Geometry.Covers),
        nameof(Geometry.CoveredBy),
        nameof(Geometry.Touches),
        nameof(Geometry.Crosses),
        nameof(Geometry.Overlaps),
        nameof(Geometry.Disjoint),
        nameof(Geometry.EqualsTopologically)
    ];

    [Theory]
    [MemberData(nameof(Predicates))]
    public void Predicate_matches_NetTopologySuite_in_both_directions(string predicate)
    {
        using var db = new TestDatabase();
        using var ctx = SpatialOnly(db);
        Seed(ctx);
        var all = ctx.Regions.AsNoTracking().ToList();
        var method = typeof(Geometry).GetMethod(predicate, [typeof(Geometry)])!;

        foreach (var wkt in queries)
        {
            var q = Geo.Wkt(wkt);

            var expected = Oracle(all, r => (bool)method.Invoke(r.Shape, [q])!);
            var expectedReversed = Oracle(all, r => (bool)method.Invoke(q, [r.Shape])!);

            var indexed = ctx.Regions.Where(Build(predicate, nameof(Region.Shape), q, false));
            var plain = ctx.Regions.Where(Build(predicate, nameof(Region.Plain), q, false));
            var reversed = ctx.Regions.Where(Build(predicate, nameof(Region.Shape), q, true));

            AssertSame(expected, indexed);
            AssertSame(expected, plain);
            AssertSame(expectedReversed, reversed);

            // Every predicate except Disjoint implies bbox overlap, so it is pre-filtered by the R*Tree.
            var usesIndex = predicate != nameof(Geometry.Disjoint);
            Assert.Equal(usesIndex, indexed.ToQueryString().Contains("\"Regions_Shape_rtree\""));
            Assert.Equal(usesIndex, reversed.ToQueryString().Contains("\"Regions_Shape_rtree\""));
            Assert.DoesNotContain("rtree", plain.ToQueryString());
        }
    }

    [Fact]
    public void Query_plan_drives_from_the_rtree()
    {
        using var db = new TestDatabase();
        using var ctx = SpatialOnly(db);
        Seed(ctx);
        var area = Geo.Wkt("POLYGON ((1 1, 2 1, 2 2, 1 2, 1 1))");

        var plan = Geo.Plan(ctx.Regions.Where(r => r.Shape!.Intersects(area)));
        Assert.Contains("SEARCH r USING INTEGER PRIMARY KEY", plan);
        Assert.Contains("VIRTUAL TABLE INDEX", plan);

        var unindexed = Geo.Plan(ctx.Regions.Where(r => r.Plain!.Intersects(area)));
        Assert.Contains("SCAN r", unindexed);
        Assert.DoesNotContain("VIRTUAL TABLE", unindexed);
    }

    [Fact]
    public void Round_trip_keeps_geometry_type_srid_and_z()
    {
        using var db = new TestDatabase();
        int id;
        using (var ctx = SpatialOnly(db))
        {
            var region = new Region
            {
                Name = "z",
                Shape = new Point(1.5, 2.5, 3.5) { SRID = 4326 },
                Plain = Geo.Wkt("MULTIPOLYGON (((0 0, 1 0, 1 1, 0 0)))"),
                Center = Geo.Point(-122.68, 45.52)
            };
            ctx.Regions.Add(region);
            ctx.SaveChanges();
            id = region.Id;
        }

        using var read = SpatialOnly(db, ensureCreated: false);
        var loaded = read.Regions.Single(r => r.Id == id);
        var point = Assert.IsType<Point>(loaded.Shape);
        Assert.Equal(4326, point.SRID);
        Assert.Equal(3.5, point.Z);
        Assert.IsType<MultiPolygon>(loaded.Plain);
        Assert.Equal(-122.68, loaded.Center!.X);
        Assert.Equal(4326, loaded.Center.SRID);
    }

    [Fact]
    public void Empty_and_null_geometries_store_without_index_rows()
    {
        using var db = new TestDatabase();
        using var ctx = SpatialOnly(db);
        ctx.Regions.Add(new Region { Name = "empty", Shape = Point.Empty });
        ctx.Regions.Add(new Region { Name = "null", Shape = null });
        ctx.SaveChanges();

        Assert.Equal(0, Geo.Scalar(ctx, "SELECT COUNT(*) FROM \"Regions_Shape_rtree\""));
        Assert.True(ctx.Regions.AsNoTracking().Single(r => r.Name == "empty").Shape!.IsEmpty);
        Assert.Empty(ctx.Regions.Where(r => r.Shape!.Intersects(Geo.Point(0, 0))).ToList());
        Assert.Equal(["empty"], ctx.Regions.Where(r => r.Shape!.IsEmpty).Select(r => r.Name).ToList());
    }

    [Fact]
    public void Planar_distance_and_IsWithinDistance_match_NetTopologySuite()
    {
        using var db = new TestDatabase();
        using var ctx = SpatialOnly(db);
        Seed(ctx);
        var all = ctx.Regions.AsNoTracking().Where(r => r.Shape != null).ToList();
        var origin = Geo.Point(1, 9);

        var expectedOrder = all.OrderBy(r => r.Shape!.Distance(origin)).ThenBy(r => r.Id).Select(r => r.Id).ToList();
        var sqlOrder = ctx.Regions.Where(r => r.Shape != null).OrderBy(r => r.Shape!.Distance(origin)).ThenBy(r => r.Id).Select(r => r.Id).ToList();
        Assert.Equal(expectedOrder, sqlOrder);

        var distances = ctx.Regions.Where(r => r.Shape != null).Select(r => new { r.Id, D = r.Shape!.Distance(origin) }).ToList();
        foreach (var d in distances)
            Assert.Equal(all.Single(r => r.Id == d.Id).Shape!.Distance(origin), d.D, 9);

        foreach (var radius in new[] { 0.5, 2.0, 5.0, 12.0 })
        {
            var expected = all.Where(r => r.Shape!.IsWithinDistance(origin, radius)).Select(r => r.Id).Order().ToList();
            var query = ctx.Regions.Where(r => r.Shape!.IsWithinDistance(origin, radius));
            Assert.Equal(expected, query.Select(r => r.Id).Order().ToList());
            Assert.Contains("\"Regions_Shape_rtree\"", query.ToQueryString());

            var where = ctx.Regions.Where(r => r.Shape!.Distance(origin) <= radius).Select(r => r.Id).Order().ToList();
            Assert.Equal(expected, where);
        }
    }

    [Fact]
    public void Member_translations_match_NetTopologySuite()
    {
        using var db = new TestDatabase();
        using var ctx = SpatialOnly(db);
        Seed(ctx);
        var all = ctx.Regions.AsNoTracking().Where(r => r.Shape != null).ToList();

        var rows = ctx.Regions
            .Where(r => r.Shape != null)
            .Select(r => new { r.Id, Area = r.Shape!.Area, Length = r.Shape.Length, Srid = r.Shape.SRID, Empty = r.Shape.IsEmpty, X = r.Center!.X, Y = r.Center.Y })
            .ToList();

        foreach (var row in rows)
        {
            var source = all.Single(r => r.Id == row.Id);
            Assert.Equal(source.Shape!.Area, row.Area, 9);
            Assert.Equal(source.Shape.Length, row.Length, 9);
            Assert.Equal(source.Shape.SRID, row.Srid);
            Assert.Equal(source.Shape.IsEmpty, row.Empty);
            Assert.Equal(source.Center!.X, row.X, 9);
            Assert.Equal(source.Center.Y, row.Y, 9);
        }

        var big = ctx.Regions.Where(r => r.Shape!.Area > 20).Select(r => r.Id).Order().ToList();
        Assert.Equal(all.Where(r => r.Shape!.Area > 20).Select(r => r.Id).Order(), big);
    }

    [Fact]
    public void Point_column_compares_against_other_geometry_subtypes()
    {
        using var db = new TestDatabase();
        using var ctx = SpatialOnly(db);
        Seed(ctx);
        var polygon = (Polygon)Geo.Wkt("POLYGON ((0 0, 6 0, 6 6, 0 6, 0 0))");
        var line = (LineString)Geo.Wkt("LINESTRING (0 0, 10 10)");
        var all = ctx.Regions.AsNoTracking().ToList();

        Assert.Equal(
            all.Where(r => r.Center != null && r.Center.Within(polygon)).Select(r => r.Id).Order(),
            ctx.Regions.Where(r => r.Center!.Within(polygon)).Select(r => r.Id).Order().ToList());
        Assert.Equal(
            all.Where(r => r.Center != null && line.Intersects(r.Center)).Select(r => r.Id).Order(),
            ctx.Regions.Where(r => line.Intersects(r.Center!)).Select(r => r.Id).Order().ToList());
    }

    [Fact]
    public void Prefilter_composes_with_filters_ordering_paging_and_projection()
    {
        using var db = new TestDatabase();
        using var ctx = SpatialOnly(db);
        Seed(ctx);
        var area = Geo.Wkt("POLYGON ((-1 -1, 11 -1, 11 11, -1 11, -1 -1))");
        var all = ctx.Regions.AsNoTracking().ToList();

        var expected = all
            .Where(r => r.Category == "even" && r.Shape != null && r.Shape.Intersects(area))
            .OrderByDescending(r => r.Name)
            .Skip(1)
            .Take(3)
            .Select(r => new { r.Name, r.Shape!.Area })
            .ToList();

        var query = ctx.Regions
            .Where(r => r.Category == "even" && r.Shape!.Intersects(area))
            .OrderByDescending(r => r.Name)
            .Skip(1)
            .Take(3)
            .Select(r => new { r.Name, r.Shape!.Area });

        Assert.Equal(expected, query.ToList());
        var sql = query.ToQueryString();
        Assert.Contains("\"Regions_Shape_rtree\"", sql);
        Assert.Contains("LIMIT", sql);

        // OR with an unindexed condition still returns the exact set (the pre-filter is AND-ed per predicate).
        var either = ctx.Regions.Where(r => r.Shape!.Intersects(area) || r.Name == "null").Select(r => r.Id).Order().ToList();
        Assert.Equal(all.Where(r => (r.Shape != null && r.Shape.Intersects(area)) || r.Name == "null").Select(r => r.Id).Order(), either);

        // Negation: NOT (prefilter AND predicate) == NOT predicate for every non-null shape.
        var outside = ctx.Regions.Where(r => r.Shape != null && !r.Shape.Intersects(area)).Select(r => r.Id).Order().ToList();
        Assert.Equal(all.Where(r => r.Shape != null && !r.Shape.Intersects(area)).Select(r => r.Id).Order(), outside);

        // Async + Any/Count.
        Assert.True(ctx.Regions.Any(r => r.Shape!.Contains(Geo.Point(51, 51))));
        Assert.Equal(all.Count(r => r.Shape != null && r.Shape.Intersects(area)), ctx.Regions.Count(r => r.Shape!.Intersects(area)));
    }

    [Fact]
    public async Task Async_query_and_navigation_free_subquery_work()
    {
        using var db = new TestDatabase();
        using var ctx = SpatialOnly(db);
        Seed(ctx);
        var area = Geo.Wkt("POLYGON ((1 1, 6 1, 6 6, 1 6, 1 1))");
        var ids = await ctx.Regions.Where(r => r.Shape!.Intersects(area)).Select(r => r.Id).ToListAsync(TestContext.Current.CancellationToken);
        var expected = ctx.Regions.AsNoTracking().AsEnumerable().Where(r => r.Shape != null && r.Shape.Intersects(area)).Select(r => r.Id);
        Assert.Equal(expected.Order(), ids.Order());

        // The spatial predicate inside a correlated sub-query.
        var pairs = ctx.Regions
            .Where(r => r.Name == "POINT (5 5)")
            .Select(r => ctx.Regions.Count(o => o.Shape!.Intersects(r.Shape!)))
            .Single();
        Assert.Equal(ctx.Regions.AsNoTracking().AsEnumerable().Count(o => o.Shape != null && o.Shape.Intersects(Geo.Wkt("POINT (5 5)"))), pairs);
    }

    [Fact]
    public void Triggers_keep_the_rtree_in_sync_for_every_write_path()
    {
        using var db = new TestDatabase();
        using var ctx = SpatialOnly(db);
        var region = new Region { Name = "moving", Shape = Geo.Point(1, 1) };
        ctx.Regions.Add(region);
        ctx.SaveChanges();
        Assert.Equal(1, Rows(ctx));
        Assert.True(Hits(ctx, 1, 1));

        // change tracked update
        region.Shape = Geo.Point(30, 30);
        ctx.SaveChanges();
        Assert.Equal(1, Rows(ctx));
        Assert.False(Hits(ctx, 1, 1));
        Assert.True(Hits(ctx, 30, 30));

        // in-place mutation of the geometry is detected by the value comparer
        ((Point)region.Shape).X = 31;
        ctx.SaveChanges();
        Assert.True(Hits(ctx, 31, 30));
        Assert.False(Hits(ctx, 30, 30));

        // set to null removes the index row
        region.Shape = null;
        ctx.SaveChanges();
        Assert.Equal(0, Rows(ctx));

        // ExecuteUpdate bypasses the change tracker - the trigger still fires
        var target = Geo.Point(7, 7);
        ctx.Regions.Where(r => r.Id == region.Id).ExecuteUpdate(s => s.SetProperty(r => r.Shape, target));
        Assert.Equal(1, Rows(ctx));
        Assert.True(Hits(ctx, 7, 7));

        // raw SQL insert
        var wkb = new NetTopologySuite.IO.WKBWriter(NetTopologySuite.IO.ByteOrder.LittleEndian, true).Write(Geo.Point(-5, -5));
        ctx.Database.ExecuteSql($"INSERT INTO \"Regions\" (\"Name\", \"Shape\") VALUES ('raw', {wkb})");
        Assert.Equal(2, Rows(ctx));
        Assert.True(Hits(ctx, -5, -5));

        // ExecuteDelete
        ctx.Regions.Where(r => r.Name == "raw").ExecuteDelete();
        Assert.Equal(1, Rows(ctx));

        // tracked delete
        ctx.ChangeTracker.Clear();
        ctx.Regions.Remove(ctx.Regions.Single());
        ctx.SaveChanges();
        Assert.Equal(0, Rows(ctx));
    }

    [Fact]
    public void Externally_opened_connection_gets_the_functions()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<SpatialOnlyContext>().UseSqlite(connection, o => o.UseShinySpatial()).Options;
        using var ctx = new SpatialOnlyContext(options);
        ctx.Database.EnsureCreated();
        Seed(ctx);

        var area = Geo.Wkt("POLYGON ((1 1, 6 1, 6 6, 1 6, 1 1))");
        Assert.NotEmpty(ctx.Regions.Where(r => r.Shape!.Intersects(area)).ToList());
    }

    [Fact]
    public void Raw_connections_need_RegisterFunctions_for_indexed_writes()
    {
        using var db = new TestDatabase();
        using (SpatialOnly(db))
        {
        }

        var wkb = new NetTopologySuite.IO.WKBWriter().Write(Geo.Point(1, 2));
        using var raw = db.OpenRaw();
        using var insert = raw.CreateCommand();
        insert.CommandText = "INSERT INTO \"Regions\" (\"Name\", \"Shape\") VALUES ('raw', $g)";
        insert.Parameters.AddWithValue("$g", wkb);
        var error = Assert.Throws<SqliteException>(() => insert.ExecuteNonQuery());
        Assert.Contains("shiny_st_bbox", error.Message);

        SqliteSpatial.RegisterFunctions(raw);
        insert.ExecuteNonQuery();

        using var count = raw.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM \"Regions_Shape_rtree\" WHERE minx <= 1 AND maxx >= 1";
        Assert.Equal(1L, count.ExecuteScalar());

        using var udf = raw.CreateCommand();
        udf.CommandText = "SELECT shiny_st_x(\"Shape\"), shiny_st_intersects(\"Shape\", NULL) FROM \"Regions\"";
        using var reader = udf.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(1.0, reader.GetDouble(0));
        Assert.True(reader.IsDBNull(1));

        Assert.Throws<ArgumentNullException>(() => SqliteSpatial.RegisterFunctions(null!));
    }

    [Fact]
    public void HasData_seeds_geometry_literals_and_indexes_them()
    {
        using var db = new TestDatabase();
        using var ctx = db.Create(o => new SeededSpatialContext(o), o => o.UseShinySpatial());

        var region = ctx.Regions.Single();
        Assert.Equal(Geo.Point(3, 4), region.Shape);
        Assert.Equal(1, Rows(ctx));
        Assert.True(Hits(ctx, 3, 4));
    }

    // ── geodesic ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GeoDistanceMeters_is_great_circle_distance()
    {
        using var db = new TestDatabase();
        using var ctx = SpatialOnly(db);
        var portland = Geo.Point(-122.6765, 45.5231);
        var seattle = Geo.Point(-122.3321, 47.6062);
        ctx.Regions.Add(new Region { Name = "seattle", Shape = seattle, Center = seattle });
        ctx.SaveChanges();

        var meters = ctx.Regions.Select(r => EF.Functions.GeoDistanceMeters(r.Center, portland)).Single();
        Assert.InRange(meters, 233_000, 235_000);

        // Same value client-side (both arguments are parameters, so EF evaluates it in memory).
        var client = ctx.Regions.Select(r => EF.Functions.GeoDistanceMeters(seattle, portland)).Single();
        Assert.Equal(meters, client, 6);
        Assert.Equal(meters, EF.Functions.GeoDistanceMeters(seattle, portland), 6);

        // Point inside a polygon is 0; a point to a line measures to the nearest edge, not the nearest vertex.
        var square = Geo.Wkt("POLYGON ((-123 45, -122 45, -122 46, -123 46, -123 45))");
        Assert.Equal(0, EF.Functions.GeoDistanceMeters(portland, square));
        var meridian = Geo.Wkt("LINESTRING (0 -10, 0 10)");
        Assert.InRange(EF.Functions.GeoDistanceMeters(Geo.Point(1, 0), meridian), 111_000, 111_400);

        Assert.True(EF.Functions.IsWithinMeters(seattle, portland, 240_000));
        Assert.False(EF.Functions.IsWithinMeters(seattle, portland, 200_000));
        Assert.Throws<ArgumentNullException>(() => EF.Functions.GeoDistanceMeters(null, portland));
        Assert.Throws<ArgumentNullException>(() => EF.Functions.IsWithinMeters(seattle, null, 1));
    }

    [Fact]
    public void IsWithinMeters_on_an_indexed_property_matches_the_haversine_oracle()
    {
        using var db = new TestDatabase();
        using var ctx = SpatialOnly(db);
        var random = new Random(42);
        for (var i = 0; i < 400; i++)
        {
            var p = Geo.Point(-123 + random.NextDouble() * 2, 45 + random.NextDouble() * 2);
            ctx.Regions.Add(new Region { Name = "p" + i, Shape = p, Plain = p });
        }
        // A polygon whose nearest edge (not vertex) is within range.
        ctx.Regions.Add(new Region { Name = "poly", Shape = Geo.Wkt("POLYGON ((-121.5 44, -121.4 44, -121.4 48, -121.5 48, -121.5 44))") });
        ctx.SaveChanges();

        var center = Geo.Point(-122, 46);
        var all = ctx.Regions.AsNoTracking().ToList();
        foreach (var radius in new[] { 5_000d, 25_000d, 60_000d, 120_000d })
        {
            var expected = all.Where(r => EF.Functions.IsWithinMeters(r.Shape, center, radius)).Select(r => r.Id).Order().ToList();
            var query = ctx.Regions.Where(r => EF.Functions.IsWithinMeters(r.Shape, center, radius));
            Assert.Equal(expected, query.Select(r => r.Id).Order().ToList());
            Assert.Contains("\"Regions_Shape_rtree\"", query.ToQueryString());

            // argument order doesn't matter for the pre-filter
            var reversed = ctx.Regions.Where(r => EF.Functions.IsWithinMeters(center, r.Shape, radius)).Select(r => r.Id).Order().ToList();
            Assert.Equal(expected, reversed);

            var byDistance = ctx.Regions.Where(r => EF.Functions.GeoDistanceMeters(r.Shape, center) <= radius).Select(r => r.Id).Order().ToList();
            Assert.Equal(expected, byDistance);
        }

        Assert.Contains(all.Single(r => r.Name == "poly").Id, ctx.Regions.Where(r => EF.Functions.IsWithinMeters(r.Shape, center, 60_000)).Select(r => r.Id).ToList());

        // nearest-first ordering by meters
        var nearest = ctx.Regions.OrderBy(r => EF.Functions.GeoDistanceMeters(r.Shape, center)).Select(r => r.Id).Take(5).ToList();
        Assert.Equal(all.OrderBy(r => EF.Functions.GeoDistanceMeters(r.Shape, center)).Select(r => r.Id).Take(5), nearest);
    }

    [Fact]
    public void IsWithinMeters_handles_the_antimeridian_and_poles()
    {
        using var db = new TestDatabase();
        using var ctx = SpatialOnly(db);
        ctx.Regions.Add(new Region { Name = "east", Shape = Geo.Point(179.9, 0) });
        ctx.Regions.Add(new Region { Name = "west", Shape = Geo.Point(-179.9, 0) });
        ctx.Regions.Add(new Region { Name = "pole-a", Shape = Geo.Point(0, 89.95) });
        ctx.Regions.Add(new Region { Name = "pole-b", Shape = Geo.Point(180, 89.95) });
        ctx.Regions.Add(new Region { Name = "far", Shape = Geo.Point(0, 0) });
        ctx.SaveChanges();

        var east = Geo.Point(179.95, 0);
        Assert.Equal(["east", "west"], ctx.Regions.Where(r => EF.Functions.IsWithinMeters(r.Shape, east, 30_000)).Select(r => r.Name).Order().ToList());

        var nearPole = Geo.Point(90, 89.95);
        Assert.Equal(["pole-a", "pole-b"], ctx.Regions.Where(r => EF.Functions.IsWithinMeters(r.Shape, nearPole, 20_000)).Select(r => r.Name).Order().ToList());
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────────────────

    static SpatialOnlyContext SpatialOnly(TestDatabase db, bool ensureCreated = true)
        => db.Create(o => new SpatialOnlyContext(o), o => o.UseShinySpatial(), ensureCreated);

    static void Seed(SpatialOnlyContext ctx)
    {
        var i = 0;
        foreach (var wkt in shapes)
        {
            var g = Geo.Wkt(wkt);
            g.SRID = 4326;
            ctx.Regions.Add(new Region
            {
                Name = wkt,
                Category = i++ % 2 == 0 ? "even" : "odd",
                Shape = g,
                Plain = g.Copy(),
                Center = (Point)g.Centroid
            });
        }
        ctx.Regions.Add(new Region { Name = "null", Category = "even" });
        ctx.SaveChanges();
        ctx.ChangeTracker.Clear();
    }

    // NTS throws for some argument combinations (e.g. Crosses on a GeometryCollection); the SQL function must then
    // fail the same way rather than silently return a different answer. null = "NTS throws".
    static List<int>? Oracle(List<Region> all, Func<Region, bool> predicate)
    {
        try
        {
            return all.Where(r => r.Shape != null && predicate(r)).Select(r => r.Id).Order().ToList();
        }
        catch (System.Reflection.TargetInvocationException)
        {
            return null;
        }
    }

    static void AssertSame(List<int>? expected, IQueryable<Region> query)
    {
        if (expected == null)
            Assert.ThrowsAny<Exception>(() => query.Select(r => r.Id).ToList());
        else
            Assert.Equal(expected, query.Select(r => r.Id).Order().ToList());
    }

    static Expression<Func<Region, bool>> Build(string predicate, string property, Geometry query, bool reversed)
    {
        var r = Expression.Parameter(typeof(Region), "r");
        Expression column = Expression.Property(r, property);
        Expression value = Expression.Field(Expression.Constant(new Holder(query)), nameof(Holder.Query));
        var method = typeof(Geometry).GetMethod(predicate, [typeof(Geometry)])!;
        var call = reversed ? Expression.Call(value, method, column) : Expression.Call(column, method, value);
        return Expression.Lambda<Func<Region, bool>>(call, r);
    }

    static long Rows(DbContext ctx) => Geo.Scalar(ctx, "SELECT COUNT(*) FROM \"Regions_Shape_rtree\"");

    static bool Hits(DbContext ctx, double x, double y)
        => Geo.Scalar(ctx, $"SELECT COUNT(*) FROM \"Regions_Shape_rtree\" WHERE minx <= {x} AND maxx >= {x} AND miny <= {y} AND maxy >= {y}") > 0;

    sealed class Holder(Geometry query)
    {
        public readonly Geometry Query = query;
    }
}

public class SeededSpatialContext(DbContextOptions options) : DbContext(options)
{
    public DbSet<Region> Regions => this.Set<Region>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.Entity<Region>(e =>
        {
            e.HasSpatialIndex(x => x.Shape);
            e.HasData(new Region { Id = 1, Name = "seed", Shape = Geo.Point(3, 4) });
        });
}
