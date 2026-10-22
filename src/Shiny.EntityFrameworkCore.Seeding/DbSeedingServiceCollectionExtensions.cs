using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Shiny.EntityFrameworkCore.Seeding.Internal;

namespace Shiny.EntityFrameworkCore.Seeding;

/// <summary>
/// Registers seeders that a hosted service runs once (per version) at host startup.
/// </summary>
public static class DbSeedingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the startup hosted service for <typeparamref name="TContext"/> and configures what it does to the
    /// schema before seeding (<see cref="DbSeedingOptions.Initialization"/>). <c>AddDbSeeder</c> calls this for you;
    /// call it yourself to change the options, or on its own to migrate at startup with no seeders. Calling it again
    /// reconfigures the same options.
    /// </summary>
    public static IServiceCollection AddDbSeeding<TContext>(this IServiceCollection services, Action<DbSeedingOptions>? configure = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);

        var configuration = services
            .Where(x => x.ServiceType == typeof(DbSeedingConfiguration<TContext>))
            .Select(x => x.ImplementationInstance)
            .OfType<DbSeedingConfiguration<TContext>>()
            .FirstOrDefault();

        if (configuration == null)
        {
            configuration = new DbSeedingConfiguration<TContext>();
            services.AddSingleton(configuration);
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IDbSeedContextRunner, DbSeedContextRunner<TContext>>());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, DbSeedHostedService>());
        }
        configure?.Invoke(configuration.Options);
        return services;
    }

    /// <summary>
    /// Registers <typeparamref name="TSeeder"/> to run against <typeparamref name="TContext"/> at host startup. The
    /// seeder is resolved from a fresh scope each run (registered transient unless you registered it yourself), so it
    /// can take scoped constructor dependencies. Seeders for one context run in registration order.
    /// </summary>
    public static IServiceCollection AddDbSeeder<TContext, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TSeeder>(
        this IServiceCollection services)
        where TContext : DbContext
        where TSeeder : class, IDbSeeder<TContext>
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddTransient<TSeeder>();
        services.AddSingleton(new DbSeederRegistration<TContext>(sp => sp.GetRequiredService<TSeeder>()));
        return services.AddDbSeeding<TContext>();
    }

    /// <summary>Registers a pre-built seeder instance to run against <typeparamref name="TContext"/> at host startup.</summary>
    public static IServiceCollection AddDbSeeder<TContext>(this IServiceCollection services, IDbSeeder<TContext> seeder)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(seeder);
        services.AddSingleton(new DbSeederRegistration<TContext>(_ => seeder));
        return services.AddDbSeeding<TContext>();
    }

    /// <summary>
    /// Registers an inline seeder to run against <typeparamref name="TContext"/> at host startup.
    /// <paramref name="name"/> keys the history row; bump <paramref name="version"/> to re-run it.
    /// </summary>
    public static IServiceCollection AddDbSeeder<TContext>(
        this IServiceCollection services,
        string name,
        int version,
        Func<TContext, CancellationToken, Task> seed)
        where TContext : DbContext
        => services.AddDbSeeder(new DelegateDbSeeder<TContext>(name, version, seed));
}
