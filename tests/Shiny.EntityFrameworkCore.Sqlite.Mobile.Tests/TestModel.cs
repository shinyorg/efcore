using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Shiny.EntityFrameworkCore.Sqlite.Mobile.Tests;

public class Region
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Category { get; set; }

    /// <summary>R*Tree indexed.</summary>
    public Geometry? Shape { get; set; }

    /// <summary>Same value as <see cref="Shape"/>, deliberately NOT indexed - the "no prefilter" control.</summary>
    public Geometry? Plain { get; set; }

    public Point? Center { get; set; }
}

public class Doc
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public string? Body { get; set; }
    public string? Tag { get; set; }
    public float[]? Embedding { get; set; }
}

/// <summary>Vectors with no index (brute-force VectorDistance) plus a float[] opted back out to JSON.</summary>
public class Sample
{
    public int Id { get; set; }
    public string Label { get; set; } = "";
    public float Weight { get; set; }
    public float[]? Raw { get; set; }
    public float[]? Json { get; set; }
}

public class Note
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public ReadOnlyMemory<float> Vector { get; set; }
}

/// <summary>All three features on one context.</summary>
public class MobileContext(DbContextOptions options) : DbContext(options)
{
    public DbSet<Region> Regions => this.Set<Region>();
    public DbSet<Doc> Docs => this.Set<Doc>();
    public DbSet<Note> Notes => this.Set<Note>();
    public DbSet<Sample> Samples => this.Set<Sample>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Sample>().Property(x => x.Json).HasColumnType("TEXT");
        modelBuilder.Entity<Region>(e => e.HasSpatialIndex(x => x.Shape));
        modelBuilder.Entity<Doc>(e =>
        {
            e.HasVectorIndex(x => x.Embedding, 3);
            e.HasFullTextIndex(x => new { x.Title, x.Body });
        });
        modelBuilder.Entity<Note>(e => e.HasVectorIndex(x => x.Vector, 2, VectorDistanceMetric.Euclidean));
    }
}

/// <summary>Spatial only - proves the feature works without vectors (no sqlite-vec) or FTS.</summary>
public class SpatialOnlyContext(DbContextOptions options) : DbContext(options)
{
    public DbSet<Region> Regions => this.Set<Region>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.Entity<Region>(e => e.HasSpatialIndex(x => x.Shape));
}

public class Article
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string? Content { get; set; }
    public float[]? Scores { get; set; }
}

/// <summary>Full-text only; <c>float[]</c> keeps EF's default JSON mapping because vectors are off.</summary>
public class FullTextOnlyContext(DbContextOptions options) : DbContext(options)
{
    public DbSet<Article> Articles => this.Set<Article>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.Entity<Article>(e => e.HasFullTextIndex(x => new { x.Title, x.Content }, "porter unicode61"));
}

/// <summary>A temp-file SQLite database, deleted on dispose.</summary>
public sealed class TestDatabase : IDisposable
{
    public TestDatabase() => this.Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"shiny-efsqlite-{Guid.NewGuid():N}.db");

    public string Path { get; }

    public string ConnectionString => $"Data Source={this.Path};Pooling=False";

    public TContext Create<TContext>(
        Func<DbContextOptions, TContext> factory,
        Action<SqliteDbContextOptionsBuilder> features,
        bool ensureCreated = true)
        where TContext : DbContext
    {
        var options = new DbContextOptionsBuilder<TContext>()
            .UseSqlite(this.ConnectionString, features)
            .Options;
        var context = factory(options);
        if (ensureCreated)
            context.Database.EnsureCreated();
        return context;
    }

    public MobileContext Mobile(bool ensureCreated = true)
        => this.Create(o => new MobileContext(o), o => o.UseShinySpatial().UseShinyVectors().UseShinyFullText(), ensureCreated);

    public SqliteConnection OpenRaw()
    {
        var connection = new SqliteConnection(this.ConnectionString);
        connection.Open();
        return connection;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            try
            {
                File.Delete(this.Path + suffix);
            }
            catch (IOException)
            {
                // best effort
            }
        }
    }
}

static class Geo
{
    static readonly WKTReader reader = new();

    public static Geometry Wkt(string wkt) => reader.Read(wkt);

    public static Point Point(double x, double y, int srid = 4326) => new(x, y) { SRID = srid };

    public static long Scalar(DbContext context, string sql)
    {
        var connection = context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    public static List<string> Strings(DbContext context, string sql)
    {
        var connection = context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var list = new List<string>();
        while (reader.Read())
            list.Add(reader.IsDBNull(0) ? "" : reader.GetValue(0).ToString()!);
        return list;
    }

    /// <summary>Runs <c>EXPLAIN QUERY PLAN</c> for an EF query, with its real parameters.</summary>
    public static string Plan<T>(IQueryable<T> query)
    {
        using var command = query.CreateDbCommand();
        if (command.Connection!.State != System.Data.ConnectionState.Open)
            command.Connection.Open();
        command.CommandText = "EXPLAIN QUERY PLAN " + command.CommandText;
        using var reader = command.ExecuteReader();
        var lines = new List<string>();
        while (reader.Read())
            lines.Add(reader.GetString(3));
        return string.Join("\n", lines);
    }
}
