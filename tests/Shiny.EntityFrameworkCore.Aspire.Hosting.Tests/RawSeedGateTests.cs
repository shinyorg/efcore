using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Shiny.EntityFrameworkCore.Aspire.Hosting.Internal;
using Shiny.EntityFrameworkCore.Seeding;

namespace Shiny.EntityFrameworkCore.Aspire.Hosting.Tests;

/// <summary>
/// The raw gate (<c>WithDbSeeder</c>): the callback gets a connection string and a Recreate flag and owns schema and
/// wipe; the gate owns only the marker, reached through a model-less context.
/// </summary>
public class RawSeedGateTests(DatabaseFixture fixture)
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>An existing, empty database, which is what an Aspire database resource hands the gate.</summary>
    async Task<TestDatabase> NewDatabaseAsync(string provider)
    {
        var db = await fixture.NewDatabaseAsync(provider);
        await using var ctx = db.CreateMarkerContext();
        await ctx.GetService<IRelationalDatabaseCreator>().CreateAsync(Ct);
        return db;
    }

    static async Task<List<bool>> StartAsync(TestDatabase db, DbSeedMode mode, int times = 1, Func<DbSeedGateContext, Task>? onSeed = null)
    {
        var invocations = new List<bool>();
        for (var i = 0; i < times; i++)
        {
            await DbSeedGate.RunAsync(
                "appdb",
                db.ConnectionString,
                db.CreateMarkerContext,
                mode,
                async (gate, _) =>
                {
                    invocations.Add(gate.Recreate);
                    if (onSeed != null)
                        await onSeed(gate);
                },
                NullLogger.Instance,
                Ct);
        }
        return invocations;
    }

    [Theory]
    [MemberData(nameof(DatabaseFixture.AllProviders), MemberType = typeof(DatabaseFixture))]
    public async Task DecisionMatrix(string provider)
    {
        var first = await this.NewDatabaseAsync(provider);
        Assert.Equal([false], await StartAsync(first, DbSeedMode.FirstTimeOnly, times: 3));

        var second = await this.NewDatabaseAsync(provider);
        Assert.Equal([false, true, true], await StartAsync(second, DbSeedMode.Recreate, times: 3));

        await using (var ctx = second.CreateMarkerContext())
            Assert.NotNull(await DbSeedHistory.FindAsync(ctx, "aspire:appdb", Ct));
    }

    [Theory]
    [MemberData(nameof(DatabaseFixture.AllProviders), MemberType = typeof(DatabaseFixture))]
    public async Task ACallbackThatDropsAndRebuildsTheDatabase_StillGetsAMarker(string provider)
    {
        var db = await this.NewDatabaseAsync(provider);

        async Task Rebuild(DbSeedGateContext gate)
        {
            await using var ctx = db.CreateContext();
            if (gate.Recreate)
                await ctx.Database.EnsureDeletedAsync(Ct);
            await ctx.Database.EnsureCreatedAsync(Ct); // must precede the marker table, which the gate writes afterwards
            ctx.Products.Add(new Product { Sku = Guid.NewGuid().ToString("N"), Name = "raw" });
            await ctx.SaveChangesAsync(Ct);
        }

        Assert.Equal([false, true], await StartAsync(db, DbSeedMode.Recreate, times: 2, onSeed: Rebuild));

        await using var check = db.CreateContext();
        Assert.Equal(1, await check.Products.CountAsync(Ct));
        Assert.NotNull(await DbSeedHistory.FindAsync(check, "aspire:appdb", Ct));
    }

    [Fact]
    public async Task AFailedSeed_LeavesNoMarker()
    {
        var db = await this.NewDatabaseAsync(DatabaseFixture.Sqlite);

        await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(db, DbSeedMode.FirstTimeOnly, onSeed: _ => throw new InvalidOperationException("boom")));

        Assert.Equal([false], await StartAsync(db, DbSeedMode.FirstTimeOnly));
    }

    [Fact]
    public async Task ACallbackThatLeavesNoDatabase_FailsLoudly()
    {
        var db = await this.NewDatabaseAsync(DatabaseFixture.Postgres);
        await StartAsync(db, DbSeedMode.FirstTimeOnly);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(db, DbSeedMode.Recreate, onSeed: async _ =>
        {
            await using var ctx = db.CreateContext();
            await ctx.Database.EnsureDeletedAsync(Ct);
        }));

        Assert.Contains("left no database", ex.Message);
    }
}
