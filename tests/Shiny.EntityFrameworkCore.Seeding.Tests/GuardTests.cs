using Microsoft.EntityFrameworkCore;
using Shiny.EntityFrameworkCore.Seeding.Internal;

namespace Shiny.EntityFrameworkCore.Seeding.Tests;

public class GuardTests(DatabaseFixture fixture)
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void AnUnsupportedProvider_IsRejectedByName()
    {
        var ex = Assert.Throws<NotSupportedException>(() => SeedHistoryDialect.For("Microsoft.EntityFrameworkCore.InMemory"));
        Assert.Contains("Microsoft.EntityFrameworkCore.InMemory", ex.Message);
    }

    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore.Sqlite")]
    [InlineData("Npgsql.EntityFrameworkCore.PostgreSQL")]
    [InlineData("Microsoft.EntityFrameworkCore.SqlServer")]
    [InlineData("MySql.EntityFrameworkCore")]
    public void SupportedProviders_HaveADialect(string providerName)
        => Assert.NotNull(SeedHistoryDialect.For(providerName));

    [Fact]
    public async Task Runner_RejectsBadInput()
    {
        var db = await fixture.NewDatabaseAsync(DatabaseFixture.Sqlite);
        await using var ctx = db.CreateContext();
        var log = new SeedLog();

        await Assert.ThrowsAsync<ArgumentNullException>(() => DbSeedRunner.RunAsync<SeedDbContext>(null!, [], Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => DbSeedRunner.RunAsync(ctx, null!, Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => DbSeedRunner.RunAsync(ctx, [null!], Ct));

        // Names compare case-insensitively on SQL Server/MySQL, so duplicates are rejected the same way everywhere.
        var dup = await Assert.ThrowsAsync<ArgumentException>(() => DbSeedRunner.RunAsync(ctx, [new CountrySeeder(log, "Countries"), new CountrySeeder(log, "countries")], Ct));
        Assert.Contains("countries", dup.Message);

        await Assert.ThrowsAsync<ArgumentException>(() => DbSeedRunner.RunAsync(ctx, [new CountrySeeder(log, new string('n', 201))], Ct));
        Assert.Empty(log.Entries); // validation happens before anything runs
    }

    [Fact]
    public void DelegateSeeder_Guards()
    {
        Assert.Throws<ArgumentException>(() => new DelegateDbSeeder<SeedDbContext>("", 1, (_, _) => Task.CompletedTask));
        Assert.Throws<ArgumentNullException>(() => new DelegateDbSeeder<SeedDbContext>(null!, 1, (_, _) => Task.CompletedTask));
        Assert.Throws<ArgumentNullException>(() => new DelegateDbSeeder<SeedDbContext>("x", 1, null!));

        var seeder = new DelegateDbSeeder<SeedDbContext>("x", 7, (_, _) => Task.CompletedTask);
        Assert.Equal("x", seeder.Name);
        Assert.Equal(7, seeder.Version);
    }

    [Fact]
    public void AppliedAtValues_ConvertFromEveryDriverShape()
    {
        var expected = new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(expected, SeedHistoryDialect.FromDbValue(expected.ToOffset(TimeSpan.FromHours(3))));
        Assert.Equal(expected, SeedHistoryDialect.FromDbValue(new DateTime(2026, 10, 22, 12, 0, 0, DateTimeKind.Unspecified)));
        Assert.Equal(expected, SeedHistoryDialect.FromDbValue("2026-10-22T12:00:00.0000000Z"));
        Assert.Throws<InvalidOperationException>(() => SeedHistoryDialect.FromDbValue(42));
    }
}
