using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shiny.EntityFrameworkCore.Aspire.Hosting.Internal;
using Shiny.EntityFrameworkCore.Seeding;

namespace Shiny.EntityFrameworkCore.Aspire.Hosting.Tests;

/// <summary>
/// The EF seed gate (<c>WithDbContextSeeder</c>) runs on exactly two triggers, first-time setup and destructive
/// recreation, decided by a marker kept inside the database. Each "start" here is one gate run, as the AppHost would
/// do on each launch.
/// </summary>
public class SeedGateTests(DatabaseFixture fixture)
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static async Task<List<bool>> StartAsync(
        TestDatabase db,
        DbSeedMode mode,
        int times = 1,
        DbInitialization initialization = DbInitialization.Migrate,
        Func<GateDbContext, DbSeedGateContext, Task>? onSeed = null)
    {
        // Each entry is one callback invocation; the value is that run's Recreate flag.
        var invocations = new List<bool>();
        for (var i = 0; i < times; i++)
        {
            await DbSeedGate.RunAsync<GateDbContext>(
                "appdb",
                db.ConnectionString,
                db.CreateContext,
                mode,
                initialization,
                async (ctx, gate, _) =>
                {
                    invocations.Add(gate.Recreate);
                    if (onSeed != null)
                        await onSeed(ctx, gate);
                    else
                        ctx.Products.Add(new Product { Sku = Guid.NewGuid().ToString("N"), Name = "seeded" }); // saved by the gate
                },
                NullLogger.Instance,
                Ct);
        }
        return invocations;
    }

    static string Quote(string provider, string identifier) => provider switch
    {
        DatabaseFixture.MySql => $"`{identifier}`",
        DatabaseFixture.SqlServer => $"[{identifier}]",
        _ => $"\"{identifier}\""
    };

    static async Task<int> ProductCountAsync(TestDatabase db)
    {
        await using var ctx = db.CreateContext();
        return await ctx.Products.CountAsync(Ct);
    }

    [Theory]
    [MemberData(nameof(DatabaseFixture.AllProviders), MemberType = typeof(DatabaseFixture))]
    public async Task FirstTimeOnly_SeedsOnceThenSkips(string provider)
    {
        var db = await fixture.NewDatabaseAsync(provider);

        var runs = await StartAsync(db, DbSeedMode.FirstTimeOnly, times: 3);

        Assert.Equal([false], runs);
        Assert.Equal(1, await ProductCountAsync(db));
    }

    [Theory]
    [MemberData(nameof(DatabaseFixture.AllProviders), MemberType = typeof(DatabaseFixture))]
    public async Task Recreate_RebuildsEveryStart_AndActuallyWipesTheData(string provider)
    {
        var db = await fixture.NewDatabaseAsync(provider);

        Assert.Equal([false], await StartAsync(db, DbSeedMode.Recreate));

        // Data that arrived between starts (an app writing to its dev database) must not survive a recreate.
        await using (var ctx = db.CreateContext())
        {
            ctx.Products.Add(new Product { Sku = "stray", Name = "written by the app" });
            await ctx.SaveChangesAsync(Ct);
        }
        Assert.Equal(2, await ProductCountAsync(db));

        Assert.Equal([true, true], await StartAsync(db, DbSeedMode.Recreate, times: 2));

        // Each seed adds one random row, so exactly one row means each recreate dropped the previous ones.
        await using var check = db.CreateContext();
        var product = await check.Products.SingleAsync(Ct);
        Assert.NotEqual("stray", product.Sku);
        Assert.Contains("20260101000000_Initial", await check.Database.GetAppliedMigrationsAsync(Ct));
    }

    [Theory]
    [MemberData(nameof(DatabaseFixture.AllProviders), MemberType = typeof(DatabaseFixture))]
    public async Task DroppingTheDatabase_MakesTheNextStartAFirstTimeSetupAgain(string provider)
    {
        var db = await fixture.NewDatabaseAsync(provider);
        Assert.Equal([false], await StartAsync(db, DbSeedMode.FirstTimeOnly, times: 2));

        // The marker lives in the data, so the "volume drop" takes it along.
        await using (var ctx = db.CreateContext())
            await ctx.Database.EnsureDeletedAsync(Ct);

        Assert.Equal([false], await StartAsync(db, DbSeedMode.FirstTimeOnly));
        Assert.Equal(1, await ProductCountAsync(db));
    }

    [Theory]
    [MemberData(nameof(DatabaseFixture.AllProviders), MemberType = typeof(DatabaseFixture))]
    public async Task AFailedSeed_LeavesNoMarker_SoTheNextStartRetriesAsFirstTime(string provider)
    {
        var db = await fixture.NewDatabaseAsync(provider);

        await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(
            db,
            DbSeedMode.Recreate,
            onSeed: (_, _) => throw new InvalidOperationException("seed blew up")));

        await using (var ctx = db.CreateContext())
            Assert.Null(await DbSeedHistory.FindAsync(ctx, DbSeedGate.MarkerName("appdb", typeof(GateDbContext)), Ct));

        // Still a first-time setup (Recreate=false): the failed run never counted as seeded.
        Assert.Equal([false], await StartAsync(db, DbSeedMode.Recreate));
    }

    [Theory]
    [MemberData(nameof(DatabaseFixture.AllProviders), MemberType = typeof(DatabaseFixture))]
    public async Task Migrations_AreAppliedOnEveryStart_EvenWhenSeedingIsSkipped(string provider)
    {
        var db = await fixture.NewDatabaseAsync(provider);
        await StartAsync(db, DbSeedMode.FirstTimeOnly);

        // Simulate "a migration was added since the last start": forget it, drop its table, start again.
        await using (var ctx = db.CreateContext())
        {
            await ctx.Database.ExecuteSqlRawAsync($"DROP TABLE {Quote(provider, "Products")}", Ct);
            await ctx.Database.ExecuteSqlRawAsync($"DELETE FROM {Quote(provider, "__EFMigrationsHistory")}", Ct);
        }

        Assert.Empty(await StartAsync(db, DbSeedMode.FirstTimeOnly));
        Assert.Equal(0, await ProductCountAsync(db)); // the table is back (migrated), the seed is not (gated)
    }

    [Theory]
    [MemberData(nameof(DatabaseFixture.AllProviders), MemberType = typeof(DatabaseFixture))]
    public async Task EnsureCreatedInitialization_BuildsTheSchemaBeforeTheMarkerTableExists(string provider)
    {
        var db = await fixture.NewDatabaseAsync(provider);

        Assert.Equal([false], await StartAsync(db, DbSeedMode.FirstTimeOnly, times: 2, initialization: DbInitialization.EnsureCreated));
        Assert.Equal([true], await StartAsync(db, DbSeedMode.Recreate, initialization: DbInitialization.EnsureCreated));

        await using var ctx = db.CreateContext();
        Assert.Equal(1, await ctx.Products.CountAsync(Ct));
        Assert.Empty(await ctx.Database.GetAppliedMigrationsAsync(Ct));
    }

    [Fact]
    public async Task InitializationNone_LeavesTheSchemaToTheCallback()
    {
        var db = await fixture.NewDatabaseAsync(DatabaseFixture.Sqlite);

        var runs = await StartAsync(
            db,
            DbSeedMode.FirstTimeOnly,
            times: 2,
            initialization: DbInitialization.None,
            onSeed: async (ctx, _) =>
            {
                Assert.Empty(await ctx.Database.GetAppliedMigrationsAsync(Ct)); // the gate did not migrate
                await ctx.Database.EnsureCreatedAsync(Ct);
                ctx.Products.Add(new Product { Sku = "a", Name = "a" });
            });

        Assert.Equal([false], runs);
        Assert.Equal(1, await ProductCountAsync(db));
    }

    [Fact]
    public async Task TheCallbackReceivesTheResourceAndConnectionString()
    {
        var db = await fixture.NewDatabaseAsync(DatabaseFixture.Sqlite);
        DbSeedGateContext? captured = null;

        await StartAsync(db, DbSeedMode.FirstTimeOnly, onSeed: (_, gate) =>
        {
            captured = gate;
            return Task.CompletedTask;
        });

        Assert.Equal(new DbSeedGateContext("appdb", db.ConnectionString, false), captured);
    }

    [Fact]
    public async Task Markers_ArePerContextType_AndSeparateFromAppSeeders()
    {
        var db = await fixture.NewDatabaseAsync(DatabaseFixture.Sqlite);
        await StartAsync(db, DbSeedMode.FirstTimeOnly);

        var auditRuns = 0;
        for (var i = 0; i < 2; i++)
        {
            await DbSeedGate.RunAsync<AuditDbContext>(
                "appdb",
                db.ConnectionString,
                () =>
                {
                    var options = new DbContextOptionsBuilder<AuditDbContext>();
                    db.Configure(options);
                    return new AuditDbContext(options.Options);
                },
                DbSeedMode.FirstTimeOnly,
                DbInitialization.None,
                (_, _, _) =>
                {
                    auditRuns++;
                    return Task.CompletedTask;
                },
                NullLogger.Instance,
                Ct);
        }

        Assert.Equal(1, auditRuns);
        await using var ctx = db.CreateContext();
        Assert.Equal(
            ["aspire:appdb:AuditDbContext", "aspire:appdb:GateDbContext"],
            (await DbSeedHistory.GetAllAsync(ctx, Ct)).Select(x => x.Name));
    }

    [Fact]
    public async Task AppSeedersCanRunInsideTheGate()
    {
        var db = await fixture.NewDatabaseAsync(DatabaseFixture.Sqlite);
        var seeder = new DelegateDbSeeder<GateDbContext>("products", 1, (ctx, _) =>
        {
            ctx.Products.Add(new Product { Sku = "p1", Name = "from the app seeder" });
            return Task.CompletedTask;
        });

        await StartAsync(db, DbSeedMode.FirstTimeOnly, onSeed: (ctx, _) => DbSeedRunner.RunAsync(ctx, [seeder], Ct));

        await using var ctx = db.CreateContext();
        Assert.Equal("from the app seeder", (await ctx.Products.SingleAsync(Ct)).Name);
        Assert.NotNull(await DbSeedHistory.FindAsync(ctx, "products", Ct));
    }
}
