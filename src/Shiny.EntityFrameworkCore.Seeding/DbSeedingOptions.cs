namespace Shiny.EntityFrameworkCore.Seeding;

/// <summary>
/// Per-context settings for the startup seeding hosted service. Configure with
/// <c>services.AddDbSeeding&lt;TContext&gt;(o =&gt; ...)</c>.
/// </summary>
public sealed class DbSeedingOptions
{
    /// <summary>
    /// What the hosted service does to the schema before running this context's seeders. Defaults to
    /// <see cref="DbInitialization.None"/>, because applying migrations at app startup is a deployment choice
    /// (it needs DDL rights, and racing instances are only safe on providers with EF's migration lock).
    /// </summary>
    public DbInitialization Initialization { get; set; } = DbInitialization.None;
}
