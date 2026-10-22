namespace Shiny.EntityFrameworkCore.Seeding;

/// <summary>
/// How the database schema is brought up before seeders run.
/// </summary>
public enum DbInitialization
{
    /// <summary>Do nothing: the schema is managed elsewhere (a migration bundle, a deploy step, the app itself).</summary>
    None,

    /// <summary>Apply pending migrations with <c>Database.MigrateAsync</c>.</summary>
    Migrate,

    /// <summary>
    /// Create the schema from the model with <c>Database.EnsureCreatedAsync</c> (no migrations). Note that
    /// EnsureCreated does nothing once <i>any</i> table exists, so it must run before anything else creates one.
    /// </summary>
    EnsureCreated
}
