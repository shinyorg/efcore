using Microsoft.EntityFrameworkCore;

namespace Shiny.EntityFrameworkCore.Seeding.Tests;

/// <summary>The run-once contract of <see cref="DbSeedRunner"/>, proven on every provider.</summary>
public class RunnerTests(DatabaseFixture fixture)
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    async Task<TestDatabase> CreatedDatabaseAsync(string provider)
    {
        var db = await fixture.NewDatabaseAsync(provider);
        await using var ctx = db.CreateContext();
        await ctx.Database.EnsureCreatedAsync(Ct);
        return db;
    }

    static async Task<IReadOnlyList<string>> RunAsync(TestDatabase db, params IDbSeeder<SeedDbContext>[] seeders)
    {
        await using var ctx = db.CreateContext();
        return await DbSeedRunner.RunAsync(ctx, seeders, Ct);
    }

    static async Task<List<string>> CountriesAsync(TestDatabase db)
    {
        await using var ctx = db.CreateContext();
        return await ctx.Countries.Select(x => x.Code).OrderBy(x => x).ToListAsync(Ct);
    }

    [Theory]
    [MemberData(nameof(DatabaseFixture.AllProviders), MemberType = typeof(DatabaseFixture))]
    public async Task FirstRun_SeedsSavesAndRecordsTheMarker(string provider)
    {
        var db = await this.CreatedDatabaseAsync(provider);
        var log = new SeedLog();

        var ran = await RunAsync(db, new CountrySeeder(log));

        Assert.Equal(["countries"], ran);
        Assert.Equal(["CA", "US"], await CountriesAsync(db));

        await using var ctx = db.CreateContext();
        var marker = await DbSeedHistory.FindAsync(ctx, "countries", Ct);
        Assert.NotNull(marker);
        Assert.Equal(1, marker.Version);
        Assert.InRange(marker.AppliedAtUtc, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(5));
    }

    [Theory]
    [MemberData(nameof(DatabaseFixture.AllProviders), MemberType = typeof(DatabaseFixture))]
    public async Task ReRun_IsANoOp(string provider)
    {
        var db = await this.CreatedDatabaseAsync(provider);
        var log = new SeedLog();

        await RunAsync(db, new CountrySeeder(log));
        var second = await RunAsync(db, new CountrySeeder(log));
        var third = await RunAsync(db, new CountrySeeder(log));

        Assert.Empty(second);
        Assert.Empty(third);
        Assert.Equal(["countries:1"], log.Entries);
    }

    [Theory]
    [MemberData(nameof(DatabaseFixture.AllProviders), MemberType = typeof(DatabaseFixture))]
    public async Task VersionBump_RunsAgain_AndALowerVersionDoesNot(string provider)
    {
        var db = await this.CreatedDatabaseAsync(provider);
        var log = new SeedLog();

        await RunAsync(db, new CountrySeeder(log, version: 1));
        var bumped = await RunAsync(db, new CountrySeeder(log, version: 2, codes: ["CA", "US", "MX"]));
        var older = await RunAsync(db, new CountrySeeder(log, version: 1));

        Assert.Equal(["countries"], bumped);
        Assert.Empty(older);
        Assert.Equal(["countries:1", "countries:2"], log.Entries);
        Assert.Equal(["CA", "MX", "US"], await CountriesAsync(db));

        await using var ctx = db.CreateContext();
        Assert.Equal(2, (await DbSeedHistory.FindAsync(ctx, "countries", Ct))!.Version);
    }

    [Theory]
    [MemberData(nameof(DatabaseFixture.AllProviders), MemberType = typeof(DatabaseFixture))]
    public async Task AFailedSeeder_RollsBackItsDataAndLeavesNoMarker_SoTheNextRunRetries(string provider)
    {
        var db = await this.CreatedDatabaseAsync(provider);
        var log = new SeedLog();

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(db, new CountrySeeder(log) { Throw = true }));

        Assert.Empty(await CountriesAsync(db));
        await using (var ctx = db.CreateContext())
            Assert.Null(await DbSeedHistory.FindAsync(ctx, "countries", Ct));

        var retry = await RunAsync(db, new CountrySeeder(log));
        Assert.Equal(["countries"], retry);
        Assert.Equal(["CA", "US"], await CountriesAsync(db));
    }

    [Theory]
    [MemberData(nameof(DatabaseFixture.AllProviders), MemberType = typeof(DatabaseFixture))]
    public async Task SeedersRunInOrder_AndEarlierMarkersSurviveALaterFailure(string provider)
    {
        var db = await this.CreatedDatabaseAsync(provider);
        var log = new SeedLog();

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(
            db,
            new CountrySeeder(log, "first", codes: ["AA"]),
            new CountrySeeder(log, "second", codes: ["BB"]),
            new CountrySeeder(log, "third", codes: ["CC"]) { Throw = true },
            new CountrySeeder(log, "fourth", codes: ["DD"])));

        Assert.Equal(["first:1", "second:1", "third:1"], log.Entries);
        Assert.Equal(["AA", "BB"], await CountriesAsync(db));

        await using var ctx = db.CreateContext();
        var history = await DbSeedHistory.GetAllAsync(ctx, Ct);
        Assert.Equal(["first", "second"], history.Select(x => x.Name));

        var resumed = await DbSeedRunner.RunAsync(
            ctx,
            [
                new CountrySeeder(log, "first", codes: ["AA"]),
                new CountrySeeder(log, "second", codes: ["BB"]),
                new CountrySeeder(log, "third", codes: ["CC"]),
                new CountrySeeder(log, "fourth", codes: ["DD"])
            ],
            Ct);
        Assert.Equal(["third", "fourth"], resumed);
    }

    [Theory]
    [MemberData(nameof(DatabaseFixture.AllProviders), MemberType = typeof(DatabaseFixture))]
    public async Task DelegateSeeder_RunsOnceAndPendingChangesAreSavedForIt(string provider)
    {
        var db = await this.CreatedDatabaseAsync(provider);
        var calls = 0;
        var seeder = new DelegateDbSeeder<SeedDbContext>("inline", 1, (ctx, _) =>
        {
            calls++;
            ctx.Countries.Add(new Country { Code = "FR", Name = "France" }); // no SaveChanges: the runner does it
            return Task.CompletedTask;
        });

        await RunAsync(db, seeder);
        await RunAsync(db, seeder);

        Assert.Equal(1, calls);
        Assert.Equal(["FR"], await CountriesAsync(db));
    }

    [Theory]
    [MemberData(nameof(DatabaseFixture.AllProviders), MemberType = typeof(DatabaseFixture))]
    public async Task ACallerTransaction_IsJoined_SoRollingItBackForgetsTheSeed(string provider)
    {
        var db = await this.CreatedDatabaseAsync(provider);
        var log = new SeedLog();

        await using (var ctx = db.CreateContext())
        {
            // The history table is DDL; create it up front so the MySQL implicit commit doesn't end the test's transaction.
            await DbSeedHistory.RecordAsync(ctx, "warmup", 1, Ct);

            await using var tx = await ctx.Database.BeginTransactionAsync(Ct);
            var ran = await DbSeedRunner.RunAsync(ctx, [new CountrySeeder(log)], Ct);
            Assert.Equal(["countries"], ran);
            Assert.Same(tx, ctx.Database.CurrentTransaction);
            await tx.RollbackAsync(Ct);
        }

        Assert.Empty(await CountriesAsync(db));
        await using var check = db.CreateContext();
        Assert.Null(await DbSeedHistory.FindAsync(check, "countries", Ct));
    }

    [Theory]
    [MemberData(nameof(DatabaseFixture.AllProviders), MemberType = typeof(DatabaseFixture))]
    public async Task AContravariantSeeder_SeedsAnyContext(string provider)
    {
        var db = await this.CreatedDatabaseAsync(provider);
        var log = new SeedLog();

        await RunAsync(db, new AnyContextSeeder(log));
        await RunAsync(db, new AnyContextSeeder(log));

        Assert.Equal(["any-context:SeedDbContext"], log.Entries);
    }

    [Theory]
    [MemberData(nameof(DatabaseFixture.AllProviders), MemberType = typeof(DatabaseFixture))]
    public async Task NoSeeders_DoesNotTouchTheDatabase(string provider)
    {
        var db = await this.CreatedDatabaseAsync(provider);
        await using var ctx = db.CreateContext();

        var ran = await DbSeedRunner.RunAsync(ctx, Array.Empty<IDbSeeder<SeedDbContext>>(), Ct);

        Assert.Empty(ran);
        Assert.False(await Internal.SeedHistoryStore.TableExistsAsync(ctx, Ct));
    }

    [Theory]
    [MemberData(nameof(DatabaseFixture.ServerProviders), MemberType = typeof(DatabaseFixture))]
    public async Task ARetryingExecutionStrategy_IsHonoured(string provider)
    {
        var db = await this.CreatedDatabaseAsync(provider);
        var log = new SeedLog();
        var options = new DbContextOptionsBuilder<SeedDbContext>();
        db.Configure(options);
        switch (provider)
        {
            case DatabaseFixture.Postgres:
                options.UseNpgsql(db.ConnectionString, o => o.EnableRetryOnFailure());
                break;
            case DatabaseFixture.SqlServer:
                options.UseSqlServer(db.ConnectionString, o => o.EnableRetryOnFailure());
                break;
            case DatabaseFixture.MySql:
                options.UseMySQL(db.ConnectionString, o => o.EnableRetryOnFailure());
                break;
        }

        // Without CreateExecutionStrategy().ExecuteAsync, a retrying strategy throws on a user-initiated transaction.
        await using var ctx = new SeedDbContext(options.Options);
        var ran = await DbSeedRunner.RunAsync(ctx, [new CountrySeeder(log)], Ct);

        Assert.Equal(["countries"], ran);
    }
}
