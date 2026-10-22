using Microsoft.EntityFrameworkCore;

namespace Shiny.EntityFrameworkCore.Seeding.Tests;

/// <summary>
/// Two app instances starting against one database at the same time: the marker row serializes them, so a seeder
/// runs once and the marker write never fails or regresses.
/// </summary>
public class ConcurrencyTests(DatabaseFixture fixture)
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [MemberData(nameof(DatabaseFixture.AllProviders), MemberType = typeof(DatabaseFixture))]
    public async Task ConcurrentRunners_SeedExactlyOnce(string provider)
    {
        var db = await fixture.NewDatabaseAsync(provider);
        await using (var setup = db.CreateContext())
            await setup.Database.EnsureCreatedAsync(Ct);

        var log = new SeedLog();
        var start = new TaskCompletionSource();

        async Task<IReadOnlyList<string>> Instance()
        {
            await start.Task;
            await using var ctx = db.CreateContext();
            // The delay holds the first instance inside its transaction while the others arrive.
            return await DbSeedRunner.RunAsync(ctx, [new CountrySeeder(log) { Delay = TimeSpan.FromMilliseconds(500) }], Ct);
        }

        var instances = Enumerable.Range(0, 4).Select(_ => Task.Run(Instance, Ct)).ToArray();
        start.SetResult();
        var results = await Task.WhenAll(instances);

        Assert.Equal(["countries:1"], log.Entries);
        Assert.Equal(1, results.Count(x => x.Count == 1));

        await using var ctx = db.CreateContext();
        Assert.Equal(2, await ctx.Countries.CountAsync(Ct));
        Assert.Equal(1, (await DbSeedHistory.FindAsync(ctx, "countries", Ct))!.Version);
    }

    [Theory]
    [MemberData(nameof(DatabaseFixture.AllProviders), MemberType = typeof(DatabaseFixture))]
    public async Task ConcurrentMarkerUpserts_NeverFail_AndTheHighestVersionWins(string provider)
    {
        var db = await fixture.NewDatabaseAsync(provider);
        await using (var setup = db.CreateContext())
            await setup.Database.EnsureCreatedAsync(Ct); // the table itself is created by the racers

        var start = new TaskCompletionSource();
        var writers = Enumerable.Range(1, 8).Select(version => Task.Run(async () =>
        {
            await start.Task;
            await using var ctx = db.CreateContext();
            await DbSeedHistory.RecordAsync(ctx, "raced", version, Ct);
        }, Ct)).ToArray();

        start.SetResult();
        await Task.WhenAll(writers);

        await using var ctx = db.CreateContext();
        var history = await DbSeedHistory.GetAllAsync(ctx, Ct);
        var row = Assert.Single(history);
        Assert.Equal("raced", row.Name);
        Assert.Equal(8, row.Version);
    }
}
