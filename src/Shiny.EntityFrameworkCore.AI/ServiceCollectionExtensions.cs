using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Shiny.EntityFrameworkCore.AI.Implementation;

namespace Shiny.EntityFrameworkCore.AI;

/// <summary>DI registration for <see cref="DbContextAITools{TContext}"/>.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers a singleton <see cref="DbContextAITools{TContext}"/> exposing the entities you opt in to as
    /// <see cref="AITool"/>s. Entities and operations not listed are invisible to the model.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every tool call opens a fresh context: from <see cref="IDbContextFactory{TContext}"/> when one is registered
    /// (resolved from the call's services, so a <c>Scoped</c> factory sees the caller's request), otherwise from a
    /// new DI scope that resolves <typeparamref name="TContext"/>. Register <c>AddDbContextFactory</c> when the
    /// context's configuration depends on request state (a per-tenant connection string, for example).
    /// </para>
    /// <para>
    /// Configuration errors that can be found eagerly are thrown here (no entities registered, a request-resolved
    /// scope whose service is not registered). Errors that need the EF model — an entity the context does not map,
    /// a key-based capability on a keyless entity — are thrown when the tools are first resolved.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddDbContextAITools<TContext>(
        this IServiceCollection services,
        Action<IDbContextAIToolBuilder> configure) where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var builder = DbContextAIToolBuilder.BuildOrThrow(configure);
        builder.ValidateRequiredServices(services, nameof(AddDbContextAITools));

        services.AddSingleton(sp => DbContextAIToolsFactory.Build<TContext>(new ServiceProviderDbContextSource<TContext>(sp), builder));
        return services;
    }

    /// <summary>
    /// Builds the tools from an <see cref="IDbContextFactory{TContext}"/> without a container — MAUI, scripts, tests.
    /// Every tool call creates (and disposes) one context from <paramref name="factory"/>.
    /// </summary>
    /// <remarks>
    /// A request-resolved scope still needs <see cref="AIFunctionArguments.Services"/> on each call; without a
    /// container, prefer the static <c>Where(o =&gt; …)</c> form.
    /// </remarks>
    public static DbContextAITools<TContext> CreateAITools<TContext>(
        this IDbContextFactory<TContext> factory,
        Action<IDbContextAIToolBuilder> configure) where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(configure);

        var builder = DbContextAIToolBuilder.BuildOrThrow(configure);
        return DbContextAIToolsFactory.Build<TContext>(new FactoryDbContextSource<TContext>(factory), builder);
    }
}
