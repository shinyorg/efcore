using Microsoft.EntityFrameworkCore;

namespace Shiny.EntityFrameworkCore.Outbox;

/// <summary>
/// <see cref="IOutboxAdmin"/> over ordinary LINQ — outbox messages are ordinary entities, so there is no provider
/// code here. Resolve it from DI (registered by <c>AddDbContextOutbox</c>), or construct it over a context factory
/// where there is no container.
/// </summary>
public sealed class OutboxAdmin : IOutboxAdmin
{
    readonly OutboxContextSource contexts;
    readonly TimeProvider clock;

    /// <summary>Creates an admin over a context factory. Each call rents and disposes one context.</summary>
    public OutboxAdmin(Func<DbContext> contextFactory, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        this.contexts = new DelegateOutboxContextSource(contextFactory);
        this.clock = timeProvider ?? TimeProvider.System;
    }

    internal OutboxAdmin(OutboxContextSource contexts, TimeProvider clock)
    {
        this.contexts = contexts;
        this.clock = clock;
    }

    static IQueryable<OutboxMessage> Query(DbContext context) => context.Set<OutboxMessage>().AsNoTracking();

    static IQueryable<OutboxMessage> Pending(DbContext context, DateTimeOffset now)
        => Query(context).Where(m => m.ProcessedAt == null && m.DeadLetteredAt == null && m.AvailableAt <= now);

    static IQueryable<OutboxMessage> DeadLettered(DbContext context) => Query(context).Where(m => m.DeadLetteredAt != null);

    async Task<T> With<T>(Func<DbContext, Task<T>> work, CancellationToken cancellationToken)
    {
        await using var lease = await this.contexts.Rent(cancellationToken).ConfigureAwait(false);
        return await work(lease.Context).ConfigureAwait(false);
    }

    public Task<int> PendingCount(CancellationToken cancellationToken = default)
        => this.With(c => Pending(c, this.clock.GetUtcNow()).CountAsync(cancellationToken), cancellationToken);

    public Task<int> ScheduledCount(CancellationToken cancellationToken = default)
    {
        var now = this.clock.GetUtcNow();
        return this.With(
            c => Query(c).Where(m => m.ProcessedAt == null && m.DeadLetteredAt == null && m.AvailableAt > now).CountAsync(cancellationToken),
            cancellationToken);
    }

    public Task<int> DeadLetterCount(CancellationToken cancellationToken = default)
        => this.With(c => DeadLettered(c).CountAsync(cancellationToken), cancellationToken);

    public Task<DateTimeOffset?> OldestPendingAt(CancellationToken cancellationToken = default)
        => this.With(
            c => Pending(c, this.clock.GetUtcNow())
                .OrderBy(m => m.CreatedAt)
                .Select(m => (DateTimeOffset?)m.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken),
            cancellationToken);

    public Task<IReadOnlyList<OutboxMessage>> DeadLetters(int take = 100, CancellationToken cancellationToken = default)
        => this.With<IReadOnlyList<OutboxMessage>>(
            async c => await DeadLettered(c)
                .OrderByDescending(m => m.DeadLetteredAt)
                .ThenByDescending(m => m.Id)
                .Take(Math.Max(1, take))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false),
            cancellationToken);

    public Task<int> Requeue(IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var list = ids.Distinct(StringComparer.Ordinal).ToList();
        if (list.Count == 0)
            return Task.FromResult(0);

        // The dead-letter guard stays in the predicate even though the caller named ids explicitly: requeueing a
        // merely *scheduled* message would reset its backoff, and requeueing a *processed* one would redeliver a
        // business event that already happened. Neither is the UI's job to prevent.
        return this.With(c => this.RequeueCore(DeadLettered(c).Where(m => list.Contains(m.Id)), cancellationToken), cancellationToken);
    }

    public Task<int> RequeueAllDeadLettered(string? messageType = null, CancellationToken cancellationToken = default)
        => this.With(c =>
        {
            var query = DeadLettered(c);
            if (!string.IsNullOrWhiteSpace(messageType))
                query = query.Where(m => m.MessageType == messageType);
            return this.RequeueCore(query, cancellationToken);
        }, cancellationToken);

    Task<int> RequeueCore(IQueryable<OutboxMessage> query, CancellationToken cancellationToken)
    {
        var now = this.clock.GetUtcNow();
        // The version moves too, so a processor still holding a stale read of the message cannot overwrite the
        // requeue, and a watch sees the requeue as a new revision.
        return query.ExecuteUpdateAsync(
            s => s
                .SetProperty(m => m.DeadLetteredAt, (DateTimeOffset?)null)
                .SetProperty(m => m.Error, (string?)null)
                .SetProperty(m => m.Attempts, 0)
                .SetProperty(m => m.AvailableAt, now)
                .SetProperty(m => m.Version, m => m.Version + 1),
            cancellationToken);
    }

    public Task<int> PurgeProcessed(DateTimeOffset olderThan, CancellationToken cancellationToken = default)
        // Structurally incapable of touching a pending or dead-lettered row: only an acknowledged message has a
        // ProcessedAt at all.
        => this.With(
            c => Query(c).Where(m => m.ProcessedAt != null && m.ProcessedAt < olderThan).ExecuteDeleteAsync(cancellationToken),
            cancellationToken);
}
