using Microsoft.EntityFrameworkCore;

namespace Shiny.EntityFrameworkCore.Seeding.Tests.Infrastructure;

/// <summary>Records every seeder invocation, so tests can assert exactly what ran and in which order.</summary>
public sealed class SeedLog
{
    readonly List<string> entries = [];

    public void Add(string entry)
    {
        lock (this.entries)
            this.entries.Add(entry);
    }

    public IReadOnlyList<string> Entries
    {
        get
        {
            lock (this.entries)
                return this.entries.ToList();
        }
    }
}

/// <summary>Adds countries that aren't there yet (idempotent), optionally failing after the add.</summary>
public sealed class CountrySeeder(SeedLog log, string name = "countries", int version = 1, params string[] codes) : IDbSeeder<SeedDbContext>
{
    public string Name => name;
    public int Version => version;
    public bool Throw { get; init; }
    public TimeSpan Delay { get; init; }

    public async Task SeedAsync(SeedDbContext context, CancellationToken cancellationToken)
    {
        log.Add($"{name}:{version}");
        var wanted = codes.Length == 0 ? ["CA", "US"] : codes;
        var existing = await context.Countries.Select(x => x.Code).ToListAsync(cancellationToken);
        foreach (var code in wanted.Except(existing))
            context.Countries.Add(new Country { Code = code, Name = code });

        if (this.Delay > TimeSpan.Zero)
            await Task.Delay(this.Delay, cancellationToken);

        if (this.Throw)
        {
            // Save first so the failure has real rows to roll back.
            await context.SaveChangesAsync(cancellationToken);
            throw new InvalidOperationException("seed blew up");
        }
    }
}

/// <summary>A DI-resolved seeder with a constructor dependency.</summary>
public sealed class DiCountrySeeder(SeedLog log) : IDbSeeder<SeedDbContext>
{
    public string Name => "di-countries";
    public int Version => 1;

    public Task SeedAsync(SeedDbContext context, CancellationToken cancellationToken)
    {
        log.Add(this.Name);
        if (!context.Countries.Any(x => x.Code == "MX"))
            context.Countries.Add(new Country { Code = "MX", Name = "Mexico" });
        return Task.CompletedTask;
    }
}

/// <summary>Seeds any context; proves the contravariant <c>IDbSeeder&lt;in TContext&gt;</c>.</summary>
public sealed class AnyContextSeeder(SeedLog log) : IDbSeeder<DbContext>
{
    public string Name => "any-context";
    public int Version => 1;

    public Task SeedAsync(DbContext context, CancellationToken cancellationToken)
    {
        log.Add($"{this.Name}:{context.GetType().Name}");
        return Task.CompletedTask;
    }
}
