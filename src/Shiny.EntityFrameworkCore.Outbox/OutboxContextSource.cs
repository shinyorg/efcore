using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Shiny.EntityFrameworkCore.Outbox;

/// <summary>
/// Hands the runner and admin a short-lived <see cref="DbContext"/> per unit of work. A <c>DbContext</c> is not
/// thread-safe and its change tracker grows with use, so nothing in the outbox holds one across a pass — each
/// parallel dispatch lane rents its own.
/// </summary>
abstract class OutboxContextSource
{
    public abstract ValueTask<OutboxContextLease> Rent(CancellationToken cancellationToken);
}

/// <summary>A rented context and whatever owns it (the context itself, or the DI scope it came from).</summary>
sealed class OutboxContextLease(DbContext context, IAsyncDisposable? owner) : IAsyncDisposable
{
    public DbContext Context { get; } = context;

    public async ValueTask DisposeAsync()
    {
        await this.Context.DisposeAsync().ConfigureAwait(false);
        if (owner != null)
            await owner.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>The host-free source: a caller-supplied factory whose contexts the outbox disposes.</summary>
sealed class DelegateOutboxContextSource(Func<DbContext> factory) : OutboxContextSource
{
    public override ValueTask<OutboxContextLease> Rent(CancellationToken cancellationToken)
    {
        var context = factory() ?? throw new InvalidOperationException("The outbox context factory returned null.");
        return ValueTask.FromResult(new OutboxContextLease(context, null));
    }
}

/// <summary>
/// The DI source: a fresh scope per rental, and inside it <see cref="IDbContextFactory{TContext}"/> when one is
/// registered (so a pooled factory keeps its pooling), else the scoped <typeparamref name="TContext"/>. The scope
/// is created even on the factory path so a scoped factory registration resolves too.
/// </summary>
sealed class ServiceOutboxContextSource<TContext>(IServiceScopeFactory scopes) : OutboxContextSource where TContext : DbContext
{
    public override async ValueTask<OutboxContextLease> Rent(CancellationToken cancellationToken)
    {
        var scope = scopes.CreateAsyncScope();
        try
        {
            var factory = scope.ServiceProvider.GetService<IDbContextFactory<TContext>>();
            DbContext context = factory != null
                ? await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false)
                : scope.ServiceProvider.GetRequiredService<TContext>();
            return new OutboxContextLease(context, scope);
        }
        catch
        {
            await scope.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

/// <summary>The checks and lookups every entry point shares.</summary>
static class OutboxGuard
{
    /// <summary>Fallback options for contexts created outside DI (tests, mobile) — enqueue depends only on the
    /// payload serializer, whose default is correct there.</summary>
    public static OutboxOptions DefaultOptions { get; } = new();

    /// <summary>
    /// Throws unless the context can hold an outbox: a relational provider (the in-memory provider has no
    /// transactions, so an "outbox" there would be a dual write with extra steps) and a model that maps
    /// <see cref="OutboxMessage"/>.
    /// </summary>
    public static void EnsureSupported(DbContext context)
    {
        if (!context.Database.IsRelational())
            throw new NotSupportedException(
                $"The transactional outbox requires a relational provider, and '{context.GetType().Name}' is using '{context.Database.ProviderName}'. " +
                "Without a real database transaction the event and the write that caused it are a dual write — the exact failure an outbox exists to prevent.");

        if (context.Model.FindEntityType(typeof(OutboxMessage)) == null)
            throw new InvalidOperationException(
                $"'{context.GetType().Name}' does not map {nameof(OutboxMessage)}. Call modelBuilder.AddOutbox() in OnModelCreating and add a migration.");
    }

    static IServiceProvider? ApplicationServices(DbContext context)
        => context.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()?.ApplicationServiceProvider;

    /// <summary>The options registered for this context's type by <c>AddDbContextOutbox</c>, else the defaults.</summary>
    public static OutboxOptions ResolveOptions(DbContext context)
        => ApplicationServices(context)?.GetKeyedService<OutboxOptions>(context.GetType()) ?? DefaultOptions;

    public static TimeProvider ResolveClock(DbContext context)
        => ApplicationServices(context)?.GetService<TimeProvider>() ?? TimeProvider.System;
}
