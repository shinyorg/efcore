using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace Shiny.EntityFrameworkCore.Seeding.Tests;

/// <summary><see cref="DbSeedHistory"/>: the marker table's public surface.</summary>
public class HistoryTests(DatabaseFixture fixture)
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [MemberData(nameof(DatabaseFixture.AllProviders), MemberType = typeof(DatabaseFixture))]
    public async Task Reads_NeverCreateTheTable_SoEnsureCreatedStillWorksAfterwards(string provider)
    {
        var db = await fixture.NewDatabaseAsync(provider);
        await using var ctx = db.CreateContext();
        await ctx.GetService<IRelationalDatabaseCreator>().CreateAsync(Ct); // empty database, no tables

        Assert.Empty(await DbSeedHistory.GetAllAsync(ctx, Ct));
        Assert.Null(await DbSeedHistory.FindAsync(ctx, "anything", Ct));
        Assert.False(await DbSeedHistory.RemoveAsync(ctx, "anything", Ct));

        // EnsureCreated bails out when any table exists, so a read that created ours would break it.
        Assert.True(await ctx.Database.EnsureCreatedAsync(Ct));
        Assert.Equal(0, await ctx.Countries.CountAsync(Ct));
    }

    [Theory]
    [MemberData(nameof(DatabaseFixture.AllProviders), MemberType = typeof(DatabaseFixture))]
    public async Task Record_Find_GetAll_Remove_RoundTrip(string provider)
    {
        var db = await fixture.NewDatabaseAsync(provider);
        await using var ctx = db.CreateContext();
        await ctx.Database.EnsureCreatedAsync(Ct);

        await DbSeedHistory.RecordAsync(ctx, "b", 3, Ct);
        await DbSeedHistory.RecordAsync(ctx, "a", 1, Ct);

        var all = await DbSeedHistory.GetAllAsync(ctx, Ct);
        Assert.Equal([("a", 1), ("b", 3)], all.Select(x => (x.Name, x.Version)));

        Assert.True(await DbSeedHistory.RemoveAsync(ctx, "b", Ct));
        Assert.False(await DbSeedHistory.RemoveAsync(ctx, "b", Ct));
        Assert.Null(await DbSeedHistory.FindAsync(ctx, "b", Ct));

        // Forgetting a marker makes its seeder run again.
        var log = new SeedLog();
        await DbSeedHistory.RecordAsync(ctx, "countries", 1, Ct);
        Assert.Empty(await DbSeedRunner.RunAsync(ctx, [new CountrySeeder(log)], Ct));
        await DbSeedHistory.RemoveAsync(ctx, "countries", Ct);
        Assert.Equal(["countries"], await DbSeedRunner.RunAsync(ctx, [new CountrySeeder(log)], Ct));
    }

    [Theory]
    [MemberData(nameof(DatabaseFixture.AllProviders), MemberType = typeof(DatabaseFixture))]
    public async Task Record_NeverLowersAVersion_ButRefreshesTheSameOne(string provider)
    {
        var db = await fixture.NewDatabaseAsync(provider);
        await using var ctx = db.CreateContext();
        await ctx.Database.EnsureCreatedAsync(Ct);

        await DbSeedHistory.RecordAsync(ctx, "countries", 5, Ct);
        var first = (await DbSeedHistory.FindAsync(ctx, "countries", Ct))!;

        await DbSeedHistory.RecordAsync(ctx, "countries", 2, Ct);
        var afterLower = (await DbSeedHistory.FindAsync(ctx, "countries", Ct))!;
        Assert.Equal(5, afterLower.Version);
        Assert.Equal(first.AppliedAtUtc, afterLower.AppliedAtUtc);

        await Task.Delay(20, Ct);
        await DbSeedHistory.RecordAsync(ctx, "countries", 5, Ct);
        var refreshed = (await DbSeedHistory.FindAsync(ctx, "countries", Ct))!;
        Assert.Equal(5, refreshed.Version);
        Assert.True(refreshed.AppliedAtUtc > first.AppliedAtUtc);

        await DbSeedHistory.RecordAsync(ctx, "countries", 9, Ct);
        Assert.Equal(9, (await DbSeedHistory.FindAsync(ctx, "countries", Ct))!.Version);
    }

    [Fact]
    public async Task Guards()
    {
        var db = await fixture.NewDatabaseAsync(DatabaseFixture.Sqlite);
        await using var ctx = db.CreateContext();

        await Assert.ThrowsAsync<ArgumentNullException>(() => DbSeedHistory.GetAllAsync(null!, Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => DbSeedHistory.FindAsync(null!, "x", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => DbSeedHistory.FindAsync(ctx, " ", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => DbSeedHistory.RecordAsync(ctx, "", 1, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => DbSeedHistory.RecordAsync(ctx, new string('x', 201), 1, Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => DbSeedHistory.RemoveAsync(null!, "x", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => DbSeedHistory.RemoveAsync(ctx, "", Ct));
        Assert.Equal("__ShinySeedHistory", DbSeedHistory.TableName);
    }
}
