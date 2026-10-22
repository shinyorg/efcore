using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Shiny.EntityFrameworkCore.Seeding.Tests;

/// <summary><c>UseVersionedSeeding</c>: EF's own Migrate/EnsureCreated seeding hook driving the versioned runner.</summary>
public class VersionedSeedingTests(DatabaseFixture fixture)
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [MemberData(nameof(DatabaseFixture.AllProviders), MemberType = typeof(DatabaseFixture))]
    public async Task MigrateAsync_RunsTheSeedersOnce(string provider)
    {
        var db = await fixture.NewDatabaseAsync(provider);
        var log = new SeedLog();

        for (var i = 0; i < 3; i++)
        {
            await using var ctx = db.CreateContext(o => o.UseVersionedSeeding(new CountrySeeder(log)));
            await ctx.Database.MigrateAsync(Ct);
        }

        Assert.Equal(["countries:1"], log.Entries);
        await using var check = db.CreateContext();
        Assert.Equal(2, await check.Countries.CountAsync(Ct));
        Assert.NotNull(await DbSeedHistory.FindAsync(check, "countries", Ct));
    }

    [Theory]
    [MemberData(nameof(DatabaseFixture.AllProviders), MemberType = typeof(DatabaseFixture))]
    public async Task SyncEnsureCreated_RunsTheSeedersOnce(string provider)
    {
        var db = await fixture.NewDatabaseAsync(provider);
        var log = new SeedLog();

        for (var i = 0; i < 2; i++)
        {
            using var ctx = db.CreateContext(o => o.UseVersionedSeeding(new CountrySeeder(log)));
            ctx.Database.EnsureCreated();
        }

        Assert.Equal(["countries:1"], log.Entries);
        await using var check = db.CreateContext();
        Assert.Equal(2, await check.Countries.CountAsync(Ct));
    }

    [Theory]
    [MemberData(nameof(DatabaseFixture.AllProviders), MemberType = typeof(DatabaseFixture))]
    public async Task AFailingSeederInsideMigrate_LeavesNoMarker(string provider)
    {
        var db = await fixture.NewDatabaseAsync(provider);
        var log = new SeedLog();

        await using (var ctx = db.CreateContext(o => o.UseVersionedSeeding(new CountrySeeder(log) { Throw = true })))
            await Assert.ThrowsAsync<InvalidOperationException>(() => ctx.Database.MigrateAsync(Ct));

        await using (var ctx = db.CreateContext(o => o.UseVersionedSeeding(new CountrySeeder(log))))
            await ctx.Database.MigrateAsync(Ct);

        Assert.Equal(["countries:1", "countries:1"], log.Entries);
        await using var check = db.CreateContext();
        Assert.Equal(1, (await DbSeedHistory.FindAsync(check, "countries", Ct))!.Version);
    }

    [Fact]
    public async Task APreviouslyConfiguredSeedingDelegate_StillRuns_First()
    {
        var db = await fixture.NewDatabaseAsync(DatabaseFixture.Sqlite);
        var log = new SeedLog();

        await using (var ctx = db.CreateContext(o => o
                         .UseAsyncSeeding((_, _, _) =>
                         {
                             log.Add("ef-async");
                             return Task.CompletedTask;
                         })
                         .UseSeeding((_, _) => log.Add("ef-sync"))
                         .UseVersionedSeeding(new CountrySeeder(log))))
        {
            await ctx.Database.EnsureCreatedAsync(Ct);
        }

        using (var ctx = db.CreateContext(o => o
                   .UseSeeding((_, _) => log.Add("ef-sync"))
                   .UseVersionedSeeding(new CountrySeeder(log))))
        {
            ctx.Database.EnsureCreated();
        }

        // The plain EF delegate runs every time; the versioned seeder only once.
        Assert.Equal(["ef-async", "countries:1", "ef-sync"], log.Entries);
    }

    [Fact]
    public async Task NonGenericBuilder_FromOnConfiguring_Works()
    {
        var db = await fixture.NewDatabaseAsync(DatabaseFixture.Sqlite);
        var log = new SeedLog();
        var builder = new DbContextOptionsBuilder<SeedDbContext>();
        db.Configure(builder);
        ((DbContextOptionsBuilder)builder).UseVersionedSeeding(new CountrySeeder(log));

        await using var ctx = new SeedDbContext(builder.Options);
        await ctx.Database.EnsureCreatedAsync(Ct);

        Assert.Equal(["countries:1"], log.Entries);
    }

    [Theory]
    [MemberData(nameof(DatabaseFixture.AllProviders), MemberType = typeof(DatabaseFixture))]
    public async Task NoSeeders_ResolvesTheDiRegistrations(string provider)
    {
        var db = await fixture.NewDatabaseAsync(provider);
        var log = new SeedLog();
        var services = new ServiceCollection()
            .AddSingleton(log)
            .AddDbContext<SeedDbContext>(o => db.Configure(o.UseVersionedSeeding<SeedDbContext>()))
            .AddDbSeeder<SeedDbContext, DiCountrySeeder>()
            .AddDbSeeder<SeedDbContext>(new CountrySeeder(log));
        await using var sp = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        for (var i = 0; i < 2; i++)
        {
            await using var scope = sp.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<SeedDbContext>().Database.MigrateAsync(Ct);
        }

        Assert.Equal(["di-countries", "countries:1"], log.Entries);
    }

    [Fact]
    public async Task ManyScopes_DoNotTripEfsServiceProviderCache()
    {
        var db = await fixture.NewDatabaseAsync(DatabaseFixture.Sqlite);
        var log = new SeedLog();
        var services = new ServiceCollection()
            .AddSingleton(log)
            .AddDbContext<SeedDbContext>(o => db.Configure(o.UseVersionedSeeding(new CountrySeeder(log))));
        await using var sp = services.BuildServiceProvider();

        // Each scope builds new options (and new seeding delegates). If those delegates were part of EF's internal
        // service provider key, the 21st provider would throw ManyServiceProvidersCreatedWarning.
        for (var i = 0; i < 30; i++)
        {
            await using var scope = sp.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<SeedDbContext>().Database.EnsureCreatedAsync(Ct);
        }

        Assert.Equal(["countries:1"], log.Entries);
    }

    [Fact]
    public async Task NoSeeders_WithoutDependencyInjection_Throws()
    {
        var db = await fixture.NewDatabaseAsync(DatabaseFixture.Sqlite);
        await using var ctx = db.CreateContext(o => o.UseVersionedSeeding());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ctx.Database.EnsureCreatedAsync(Ct));
        Assert.Contains("dependency injection", ex.Message);
    }

    [Fact]
    public async Task ConfiguredForTheWrongContextType_Throws()
    {
        var db = await fixture.NewDatabaseAsync(DatabaseFixture.Sqlite);
        var builder = new DbContextOptionsBuilder<OtherDbContext>();
        db.Configure(builder);
        ((DbContextOptionsBuilder)builder).UseVersionedSeeding(new CountrySeeder(new SeedLog()));
        await using var ctx = new OtherDbContext(builder.Options);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ctx.Database.EnsureCreatedAsync(Ct));
        Assert.Contains("OtherDbContext", ex.Message);
    }

    [Fact]
    public void Guards()
    {
        Assert.Throws<ArgumentNullException>(() => ((DbContextOptionsBuilder)null!).UseVersionedSeeding<SeedDbContext>());
        Assert.Throws<ArgumentNullException>(() => new DbContextOptionsBuilder().UseVersionedSeeding<SeedDbContext>(null!));
    }
}
