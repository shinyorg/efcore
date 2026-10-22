using Microsoft.EntityFrameworkCore;

namespace Shiny.EntityFrameworkCore.Seeding.Internal;

/// <summary>
/// One <c>AddDbSeeder</c> call: how to get the seeder for <typeparamref name="TContext"/> out of a scope. Resolving
/// per scope (rather than holding an instance) lets class seeders take scoped constructor dependencies.
/// </summary>
sealed class DbSeederRegistration<TContext>(Func<IServiceProvider, IDbSeeder<TContext>> factory) where TContext : DbContext
{
    public IDbSeeder<TContext> Resolve(IServiceProvider services) => factory(services);
}
