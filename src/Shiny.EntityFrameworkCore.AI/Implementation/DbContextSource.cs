using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Shiny.EntityFrameworkCore.AI.Implementation;

/// <summary>
/// One context for one tool call, plus whatever owns it (the context itself, or the DI scope it came from). The
/// <see cref="Services"/> are the ones the context was resolved from — what an insert hook falls back to when the
/// call carries no services of its own.
/// </summary>
sealed class DbContextLease : IAsyncDisposable, IDisposable
{
    readonly IAsyncDisposable owner;

    public DbContextLease(DbContext context, IAsyncDisposable owner, IServiceProvider? services)
    {
        this.Context = context;
        this.owner = owner;
        this.Services = services;
    }

    public DbContext Context { get; }
    public IServiceProvider? Services { get; }

    public ValueTask DisposeAsync() => this.owner.DisposeAsync();

    public void Dispose() => ((IDisposable)this.owner).Dispose();
}

/// <summary>Where a tool call gets its <see cref="DbContext"/> from.</summary>
interface IDbContextSource
{
    /// <summary>Opens a fresh context for one tool call. <paramref name="callServices"/> is the call's own scope, when it has one.</summary>
    ValueTask<DbContextLease> OpenAsync(IServiceProvider? callServices, CancellationToken cancellationToken);

    /// <summary>Synchronous open, used once while the tools are built to read the EF model.</summary>
    DbContextLease Open();
}

/// <summary>
/// The DI source: a registered <see cref="IDbContextFactory{TContext}"/> wins (resolved from the call's services, so
/// a scoped factory sees the caller's request); otherwise a brand-new DI scope resolves <typeparamref name="TContext"/>.
/// </summary>
/// <remarks>
/// It deliberately never borrows the caller's own scoped context: that one may be mid-unit-of-work, it is not safe for
/// the parallel tool calls a model makes, and anything the tool loaded would linger in its change tracker.
/// </remarks>
sealed class ServiceProviderDbContextSource<TContext>(IServiceProvider root) : IDbContextSource where TContext : DbContext
{
    public async ValueTask<DbContextLease> OpenAsync(IServiceProvider? callServices, CancellationToken cancellationToken)
    {
        var services = callServices ?? root;
        var factory = services.GetService<IDbContextFactory<TContext>>();
        if (factory != null)
        {
            var context = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            return new DbContextLease(context, context, services);
        }

        var scope = services.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
        return new DbContextLease(scope.ServiceProvider.GetRequiredService<TContext>(), scope, scope.ServiceProvider);
    }

    public DbContextLease Open()
    {
        var factory = root.GetService<IDbContextFactory<TContext>>();
        if (factory != null)
        {
            var context = factory.CreateDbContext();
            return new DbContextLease(context, context, root);
        }

        var scope = root.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
        return new DbContextLease(scope.ServiceProvider.GetRequiredService<TContext>(), scope, scope.ServiceProvider);
    }
}

/// <summary>The container-free source: one context per call from a factory the caller supplied.</summary>
sealed class FactoryDbContextSource<TContext>(IDbContextFactory<TContext> factory) : IDbContextSource where TContext : DbContext
{
    public async ValueTask<DbContextLease> OpenAsync(IServiceProvider? callServices, CancellationToken cancellationToken)
    {
        var context = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return new DbContextLease(context, context, callServices);
    }

    public DbContextLease Open()
    {
        var context = factory.CreateDbContext();
        return new DbContextLease(context, context, null);
    }
}
