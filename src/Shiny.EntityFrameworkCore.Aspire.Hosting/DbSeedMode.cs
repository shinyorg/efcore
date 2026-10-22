namespace Shiny.EntityFrameworkCore.Aspire.Hosting;

/// <summary>
/// When an AppHost seed gate (<c>WithDbContextSeeder</c> / <c>WithDbSeeder</c>) runs its callback.
/// </summary>
/// <remarks>
/// Either mode only ever seeds on one of two triggers: the <b>first time the database is set up</b>, or a
/// <b>destructive recreation</b>. The "has this database been seeded" marker lives in the database itself, so
/// destroying the data (dropping the container volume) also destroys the marker and the next AppHost start seeds again.
/// </remarks>
public enum DbSeedMode
{
    /// <summary>
    /// Seed only when the database carries no gate marker: it is brand new, or its data was destroyed since the last
    /// run. Steady-state AppHost restarts do not seed.
    /// </summary>
    FirstTimeOnly,

    /// <summary>
    /// Seed on first-time setup <b>and</b> on every later start, rebuilding first. A dev-loop convenience that
    /// destroys data on every AppHost start; never point it at anything you care about.
    /// </summary>
    Recreate
}
