namespace Shiny.EntityFrameworkCore.Aspire.Hosting;

/// <summary>
/// Passed to an AppHost seed callback once the database resource is ready and the gate has decided to seed.
/// </summary>
/// <param name="ResourceName">The database resource the gate is attached to.</param>
/// <param name="ConnectionString">The resource's resolved connection string.</param>
/// <param name="Recreate">
/// Which trigger fired. <c>false</c>: first-time setup, the database had no marker. <c>true</c>: destructive
/// recreation in <see cref="DbSeedMode.Recreate"/> mode. <c>WithDbContextSeeder</c> has already dropped and rebuilt
/// the database by then; a raw <c>WithDbSeeder</c> callback owns the wipe itself.
/// </param>
public sealed record DbSeedGateContext(string ResourceName, string ConnectionString, bool Recreate);
