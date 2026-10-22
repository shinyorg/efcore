using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Shiny.EntityFrameworkCore.Seeding.Tests;

/// <summary>The DI registrations and the startup hosted service.</summary>
public class HostedSeedingTests(DatabaseFixture fixture)
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static async Task StartAsync(Action<IServiceCollection> configure)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<SeedLog>();
        configure(builder.Services);
        using var host = builder.Build();
        await host.StartAsync(Ct);
        await host.StopAsync(Ct);
    }

    [Theory]
    [MemberData(nameof(DatabaseFixture.AllProviders), MemberType = typeof(DatabaseFixture))]
    public async Task MigrateOnStartup_AppliesMigrationsThenRunsSeedersInOrder(string provider)
    {
        var db = await fixture.NewDatabaseAsync(provider);
        var log = new SeedLog();

        for (var i = 0; i < 2; i++)
        {
            await StartAsync(services => services
                .AddSingleton(log)
                .AddDbContext<SeedDbContext>(db.Configure)
                .AddDbSeeder<SeedDbContext, DiCountrySeeder>()
                .AddDbSeeder<SeedDbContext>("inline", 1, (ctx, _) =>
                {
                    log.Add("inline");
                    ctx.Countries.Add(new Country { Code = "JP", Name = "Japan" });
                    return Task.CompletedTask;
                })
                .AddDbSeeder<SeedDbContext>(new CountrySeeder(log))
                .AddDbSeeding<SeedDbContext>(o => o.Initialization = DbInitialization.Migrate));
        }

        Assert.Equal(["di-countries", "inline", "countries:1"], log.Entries);
        await using var ctx = db.CreateContext();
        Assert.Equal(["CA", "JP", "MX", "US"], await ctx.Countries.Select(x => x.Code).OrderBy(x => x).ToListAsync(Ct));
        Assert.Contains("20260101000000_Initial", await ctx.Database.GetAppliedMigrationsAsync(Ct));
    }

    [Theory]
    [MemberData(nameof(DatabaseFixture.AllProviders), MemberType = typeof(DatabaseFixture))]
    public async Task HostedServiceAndUseVersionedSeeding_NeverDoubleRun(string provider)
    {
        var db = await fixture.NewDatabaseAsync(provider);
        var log = new SeedLog();

        // Migrate fires UseVersionedSeeding (which runs the DI seeders), then the hosted service runs the same seeders.
        await StartAsync(services => services
            .AddSingleton(log)
            .AddDbContext<SeedDbContext>(o => db.Configure(o.UseVersionedSeeding<SeedDbContext>()))
            .AddDbSeeder<SeedDbContext, DiCountrySeeder>()
            .AddDbSeeding<SeedDbContext>(o => o.Initialization = DbInitialization.Migrate));

        Assert.Equal(["di-countries"], log.Entries);
    }

    [Theory]
    [MemberData(nameof(DatabaseFixture.AllProviders), MemberType = typeof(DatabaseFixture))]
    public async Task MultipleContextTypes_EachInitializeAndSeedTheirOwnDatabase(string provider)
    {
        var seedDb = await fixture.NewDatabaseAsync(provider);
        var otherDb = await fixture.NewDatabaseAsync(provider);
        var log = new SeedLog();

        await StartAsync(services => services
            .AddSingleton(log)
            .AddDbContext<SeedDbContext>(seedDb.Configure)
            .AddDbContextFactory<OtherDbContext>(otherDb.Configure)
            .AddDbSeeder<SeedDbContext>(new CountrySeeder(log))
            .AddDbSeeder<OtherDbContext>("settings", 1, (ctx, _) =>
            {
                log.Add("settings");
                ctx.Settings.Add(new Setting { Key = "theme", Value = "dark" });
                return Task.CompletedTask;
            })
            .AddDbSeeding<SeedDbContext>(o => o.Initialization = DbInitialization.Migrate)
            .AddDbSeeding<OtherDbContext>(o => o.Initialization = DbInitialization.EnsureCreated));

        Assert.Equal(["countries:1", "settings"], log.Entries);

        await using var other = new OtherDbContext(otherDb.Options<OtherDbContext>());
        Assert.Equal("dark", (await other.Settings.SingleAsync(Ct)).Value);
        Assert.Equal(["settings"], (await DbSeedHistory.GetAllAsync(other, Ct)).Select(x => x.Name));

        await using var seed = seedDb.CreateContext();
        Assert.Equal(["countries"], (await DbSeedHistory.GetAllAsync(seed, Ct)).Select(x => x.Name));
    }

    [Fact]
    public async Task InitializationNone_IsTheDefault_AndLeavesTheSchemaAlone()
    {
        var db = await fixture.NewDatabaseAsync(DatabaseFixture.Sqlite);
        await using (var setup = db.CreateContext())
            await setup.Database.EnsureCreatedAsync(Ct);

        var log = new SeedLog();
        await StartAsync(services => services
            .AddSingleton(log)
            .AddDbContext<SeedDbContext>(db.Configure)
            .AddDbSeeder<SeedDbContext>(new CountrySeeder(log)));

        Assert.Equal(["countries:1"], log.Entries);
        await using var ctx = db.CreateContext();
        Assert.Empty(await ctx.Database.GetAppliedMigrationsAsync(Ct)); // created by EnsureCreated, never migrated
    }

    [Fact]
    public async Task AFailingSeeder_FailsHostStart()
    {
        var db = await fixture.NewDatabaseAsync(DatabaseFixture.Sqlite);
        var log = new SeedLog();

        await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(services => services
            .AddDbContext<SeedDbContext>(db.Configure)
            .AddDbSeeder<SeedDbContext>(new CountrySeeder(log) { Throw = true })
            .AddDbSeeding<SeedDbContext>(o => o.Initialization = DbInitialization.EnsureCreated)));
    }

    [Fact]
    public async Task AnUnregisteredContext_FailsHostStartWithAClearMessage()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(services => services
            .AddDbSeeder<SeedDbContext>(new CountrySeeder(new SeedLog()))));

        Assert.Contains("AddDbContext", ex.Message);
    }

    [Fact]
    public void Registration_IsIdempotent_AndAddDbSeedingReconfiguresTheSameOptions()
    {
        var services = new ServiceCollection();
        services.AddDbSeeder<SeedDbContext>("a", 1, (_, _) => Task.CompletedTask);
        services.AddDbSeeder<SeedDbContext>("b", 1, (_, _) => Task.CompletedTask);
        services.AddDbSeeding<SeedDbContext>(o => o.Initialization = DbInitialization.Migrate);
        services.AddDbSeeding<SeedDbContext>();
        services.AddDbSeeding<OtherDbContext>(o => o.Initialization = DbInitialization.EnsureCreated);

        Assert.Single(services, x => x.ServiceType == typeof(IHostedService));
        Assert.Equal(2, services.Count(x => x.ServiceType == typeof(Internal.IDbSeedContextRunner)));

        using var sp = services.BuildServiceProvider();
        Assert.Equal(DbInitialization.Migrate, sp.GetRequiredService<Internal.DbSeedingConfiguration<SeedDbContext>>().Options.Initialization);
        Assert.Equal(DbInitialization.EnsureCreated, sp.GetRequiredService<Internal.DbSeedingConfiguration<OtherDbContext>>().Options.Initialization);
        Assert.Equal(DbInitialization.None, new DbSeedingOptions().Initialization);
    }

    [Fact]
    public void Guards()
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentNullException>(() => ((IServiceCollection)null!).AddDbSeeding<SeedDbContext>());
        Assert.Throws<ArgumentNullException>(() => ((IServiceCollection)null!).AddDbSeeder<SeedDbContext, DiCountrySeeder>());
        Assert.Throws<ArgumentNullException>(() => services.AddDbSeeder<SeedDbContext>((IDbSeeder<SeedDbContext>)null!));
        Assert.Throws<ArgumentException>(() => services.AddDbSeeder<SeedDbContext>(" ", 1, (_, _) => Task.CompletedTask));
        Assert.Throws<ArgumentNullException>(() => services.AddDbSeeder<SeedDbContext>("x", 1, null!));
    }
}
