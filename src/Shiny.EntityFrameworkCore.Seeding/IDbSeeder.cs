using Microsoft.EntityFrameworkCore;

namespace Shiny.EntityFrameworkCore.Seeding;

/// <summary>
/// A unit of initial data written into a database once per <see cref="Version"/>.
/// <para>
/// EF Core's own <c>UseSeeding</c>/<c>UseAsyncSeeding</c> runs on every <c>EnsureCreated</c>/<c>Migrate</c>
/// call and leaves "have I already done this?" to you. A seeder instead records a row in the
/// <c>__ShinySeedHistory</c> table, keyed on <see cref="Name"/>, and only runs when that row is missing or its
/// version is lower than <see cref="Version"/>. Bump the version to re-run after changing the seed data.
/// </para>
/// <para>
/// The interface is contravariant, so an <c>IDbSeeder&lt;DbContext&gt;</c> can seed any context type.
/// </para>
/// </summary>
/// <typeparam name="TContext">The context the seeder writes through.</typeparam>
public interface IDbSeeder<in TContext> where TContext : DbContext
{
    /// <summary>
    /// Stable, unique name for this seeder: the primary key of its history row (200 characters max).
    /// Renaming a seeder makes it run again.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Monotonically increasing version. The seeder runs when no history row exists or the recorded version
    /// is lower than this value.
    /// </summary>
    int Version { get; }

    /// <summary>
    /// Writes the seed data. Add or update entities on <paramref name="context"/>; the runner calls
    /// <c>SaveChangesAsync</c> afterwards if anything is pending and records the history row in the same
    /// transaction. Keep the writes idempotent (check before insert, or upsert on known keys): a bumped version
    /// re-runs the seeder over data that may already be there.
    /// </summary>
    Task SeedAsync(TContext context, CancellationToken cancellationToken);
}
