using Microsoft.EntityFrameworkCore;

namespace Shiny.EntityFrameworkCore.Seeding.Internal;

/// <summary>The <see cref="DbSeedingOptions"/> for one context type, held as a singleton instance.</summary>
sealed class DbSeedingConfiguration<TContext> where TContext : DbContext
{
    public DbSeedingOptions Options { get; } = new();
}
