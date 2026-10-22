using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Shiny.EntityFrameworkCore.Outbox;

/// <summary>Registers the outbox processor and its operational surface for a context type.</summary>
public static class OutboxServiceCollectionExtensions
{
    /// <summary>
    /// Registers <typeparamref name="TDispatcher"/> (scoped, so it can take scoped dependencies), a hosted processor
    /// that drains <typeparamref name="TContext"/>'s outbox, an <see cref="OutboxRunner"/> and an
    /// <see cref="IOutboxAdmin"/> (both keyed by <c>typeof(TContext)</c>, and unkeyed for the first context
    /// registered), and the <see cref="OutboxOptions"/> that <c>Enqueue</c> and <c>AddOutboxPublishing</c> use.
    /// <code>
    /// services.AddDbContext&lt;AppDbContext&gt;(o => o.UseNpgsql(cs));
    /// services.AddDbContextOutbox&lt;AppDbContext, BusDispatcher&gt;(o => o.OrderedPartitions = true);
    /// </code>
    /// </summary>
    /// <remarks>
    /// Contexts come from <see cref="IDbContextFactory{TContext}"/> when one is registered, else from a fresh scope
    /// per unit of work. The model must call <c>modelBuilder.AddOutbox()</c>, and the provider must be relational —
    /// both are checked when the host starts.
    /// </remarks>
    public static IServiceCollection AddDbContextOutbox<TContext, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TDispatcher>(
        this IServiceCollection services,
        Action<OutboxOptions>? configure = null)
        where TContext : DbContext
        where TDispatcher : class, IOutboxDispatcher
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddKeyedScoped<IOutboxDispatcher, TDispatcher>(typeof(TContext));
        return AddCore<TContext>(services, configure);
    }

    /// <summary>
    /// Registers a delegate dispatcher — the one-liner form, for a transport that needs no injected state:
    /// <code>services.AddDbContextOutbox&lt;AppDbContext&gt;((msg, ct) => bus.Publish(msg.MessageType, msg.Payload, ct));</code>
    /// </summary>
    public static IServiceCollection AddDbContextOutbox<TContext>(
        this IServiceCollection services,
        Func<OutboxMessage, CancellationToken, Task> dispatch,
        Action<OutboxOptions>? configure = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(dispatch);
        services.TryAddKeyedSingleton<IOutboxDispatcher>(typeof(TContext), new DelegateOutboxDispatcher(dispatch));
        return AddCore<TContext>(services, configure);
    }

    static IServiceCollection AddCore<TContext>(IServiceCollection services, Action<OutboxOptions>? configure) where TContext : DbContext
    {
        var key = typeof(TContext);
        if (services.Any(d => d.IsKeyedService && Equals(d.ServiceKey, key) && d.ServiceType == typeof(OutboxRunner)))
            throw new InvalidOperationException($"AddDbContextOutbox<{key.Name}> has already been called. Configure each context's outbox once.");

        var options = new OutboxOptions();
        configure?.Invoke(options);
        options.Validate();

        services.AddKeyedSingleton<OutboxOptions>(key, options);
        services.AddKeyedSingleton<OutboxRunner>(key, (sp, _) => new OutboxRunner(
            new ServiceOutboxContextSource<TContext>(sp.GetRequiredService<IServiceScopeFactory>()),
            key.Name,
            sp.GetRequiredService<IServiceScopeFactory>(),
            key,
            options,
            sp.GetService<TimeProvider>(),
            sp.GetService<ILoggerFactory>()?.CreateLogger<OutboxRunner>()));
        services.AddKeyedSingleton<IOutboxAdmin>(key, (sp, _) => new OutboxAdmin(
            new ServiceOutboxContextSource<TContext>(sp.GetRequiredService<IServiceScopeFactory>()),
            sp.GetService<TimeProvider>() ?? TimeProvider.System));

        // The common single-context app injects these without a key.
        services.TryAddSingleton(sp => sp.GetRequiredKeyedService<OutboxRunner>(key));
        services.TryAddSingleton(sp => sp.GetRequiredKeyedService<IOutboxAdmin>(key));

        services.AddSingleton<IHostedService, OutboxProcessor<TContext>>();
        return services;
    }
}
