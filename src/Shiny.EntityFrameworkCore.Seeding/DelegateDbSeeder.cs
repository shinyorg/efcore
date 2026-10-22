using Microsoft.EntityFrameworkCore;

namespace Shiny.EntityFrameworkCore.Seeding;

/// <summary>
/// An <see cref="IDbSeeder{TContext}"/> backed by an inline delegate, for seed steps too small to deserve a class.
/// Created by the <c>AddDbSeeder(name, version, seed)</c> overload, or new it up for <see cref="DbSeedRunner"/>.
/// </summary>
public sealed class DelegateDbSeeder<TContext> : IDbSeeder<TContext> where TContext : DbContext
{
    readonly Func<TContext, CancellationToken, Task> seed;

    /// <param name="name">The history key (see <see cref="IDbSeeder{TContext}.Name"/>).</param>
    /// <param name="version">The version to record (see <see cref="IDbSeeder{TContext}.Version"/>).</param>
    /// <param name="seed">The seed step.</param>
    public DelegateDbSeeder(string name, int version, Func<TContext, CancellationToken, Task> seed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(seed);
        this.Name = name;
        this.Version = version;
        this.seed = seed;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public int Version { get; }

    /// <inheritdoc />
    public Task SeedAsync(TContext context, CancellationToken cancellationToken)
        => this.seed(context, cancellationToken);
}
