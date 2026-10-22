namespace Shiny.EntityFrameworkCore.Seeding;

/// <summary>
/// One row of the <c>__ShinySeedHistory</c> table: the highest version of a seeder that has been applied.
/// </summary>
/// <param name="Name">The seeder name (the row key).</param>
/// <param name="Version">The highest version applied.</param>
/// <param name="AppliedAtUtc">When that version was recorded.</param>
public sealed record DbSeedRecord(string Name, int Version, DateTimeOffset AppliedAtUtc);
