using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.EntityFrameworkCore;
using Shiny.EntityFrameworkCore.Seeding;

namespace Shiny.EntityFrameworkCore.Aspire.Hosting.Tests;

/// <summary>Model-only checks: the extensions attach to every database resource type and validate their input.</summary>
public class ResourceBuilderTests
{
    static IDistributedApplicationBuilder CreateBuilder() => DistributedApplication.CreateBuilder();

    static Task NoSeed(GateDbContext db, DbSeedGateContext ctx, CancellationToken ct) => Task.CompletedTask;

    [Fact]
    public void WithDbContextSeeder_AttachesToPostgresSqlServerAndMySqlDatabases()
    {
        var builder = CreateBuilder();

        var pg = builder.AddPostgres("pg").AddDatabase("pgdb");
        var sql = builder.AddSqlServer("sql").AddDatabase("sqldb");
        var my = builder.AddMySql("my").AddDatabase("mydb");

        Assert.Same(pg.Resource, pg.WithDbContextSeeder<GateDbContext>((o, cs) => o.UseNpgsql(cs), NoSeed).Resource);
        Assert.Same(sql.Resource, sql.WithDbContextSeeder<GateDbContext>((o, cs) => o.UseSqlServer(cs), NoSeed, DbSeedMode.Recreate).Resource);
        Assert.Same(my.Resource, my.WithDbContextSeeder<GateDbContext>((o, cs) => o.UseMySQL(cs), NoSeed, initialization: DbInitialization.EnsureCreated).Resource);
    }

    [Fact]
    public void WithDbSeeder_AttachesToADatabase()
    {
        var builder = CreateBuilder();
        var db = builder.AddPostgres("pg").AddDatabase("pgdb");

        var result = db.WithDbSeeder((o, cs) => o.UseNpgsql(cs), (_, _) => Task.CompletedTask, DbSeedMode.Recreate);

        Assert.IsAssignableFrom<IResourceWithConnectionString>(result.Resource);
        Assert.Same(db.Resource, result.Resource);
    }

    [Fact]
    public void RecreateWithoutInitialization_IsRejected()
    {
        var db = CreateBuilder().AddPostgres("pg").AddDatabase("pgdb");

        var ex = Assert.Throws<ArgumentException>(() => db.WithDbContextSeeder<GateDbContext>(
            (o, cs) => o.UseNpgsql(cs),
            NoSeed,
            DbSeedMode.Recreate,
            DbInitialization.None));
        Assert.Equal("initialization", ex.ParamName);
    }

    [Fact]
    public void Guards()
    {
        var db = CreateBuilder().AddPostgres("pg").AddDatabase("pgdb");

        Assert.Throws<ArgumentNullException>(() => ((IResourceBuilder<IResourceWithConnectionString>)null!).WithDbContextSeeder<GateDbContext>((o, cs) => o.UseNpgsql(cs), NoSeed));
        Assert.Throws<ArgumentNullException>(() => db.WithDbContextSeeder<GateDbContext>(null!, NoSeed));
        Assert.Throws<ArgumentNullException>(() => db.WithDbContextSeeder<GateDbContext>((o, cs) => o.UseNpgsql(cs), null!));
        Assert.Throws<ArgumentNullException>(() => ((IResourceBuilder<IResourceWithConnectionString>)null!).WithDbSeeder((o, cs) => o.UseNpgsql(cs), (_, _) => Task.CompletedTask));
        Assert.Throws<ArgumentNullException>(() => db.WithDbSeeder(null!, (_, _) => Task.CompletedTask));
        Assert.Throws<ArgumentNullException>(() => db.WithDbSeeder((o, cs) => o.UseNpgsql(cs), null!));
    }
}
