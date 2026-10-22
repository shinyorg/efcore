using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Shiny.EntityFrameworkCore.Outbox;

/// <summary>
/// The delivery half of the outbox, as a plain object you can drive yourself. The hosted processor registered by
/// <c>AddDbContextOutbox</c> is a loop around it; call it directly when there is no generic host (a MAUI app), from
/// a scheduled job, or from a test that wants one deterministic pass instead of a background timer.
/// </summary>
/// <remarks>
/// <para>
/// Claiming is per-message optimistic concurrency, not a table lock: the runner bumps
/// <see cref="OutboxMessage.Attempts"/> and <see cref="OutboxMessage.Version"/> and pushes
/// <see cref="OutboxMessage.AvailableAt"/> out by the backoff, in one <c>UPDATE … WHERE id = @id AND version =
/// @read</c>. Zero rows affected means another worker got there first, so this one moves on. That is the whole
/// concurrency design — no leases table, no leader election, and any number of workers scale by just running.
/// </para>
/// <para>
/// It also means the backoff doubles as a visibility timeout: a runner that dies mid-dispatch has already pushed
/// the message forward, so the message returns to the queue on its own rather than being stranded.
/// </para>
/// <para>
/// The runner's writes are set-based (<c>ExecuteUpdate</c>/<c>ExecuteDelete</c>), not <c>SaveChanges</c>, so your
/// <c>SaveChanges</c> interceptors (auditing, soft delete, outbox publishing) never fire for the outbox's own
/// bookkeeping, and no message is ever held in a change tracker.
/// </para>
/// </remarks>
public sealed class OutboxRunner
{
    readonly OutboxContextSource contexts;
    readonly OutboxOptions options;
    readonly TimeProvider clock;
    readonly ILogger logger;
    readonly IServiceScopeFactory? scopes;
    readonly object? dispatcherKey;
    readonly IOutboxDispatcher? dispatcher;
    readonly string contextName;

    /// <summary>Creates a runner over a fixed dispatcher — the host-free form.</summary>
    /// <param name="contextFactory">Creates a new context per unit of work. The runner disposes what it gets, and
    /// may hold up to <see cref="OutboxOptions.MaxParallelism"/> at once, so never return a shared instance.</param>
    /// <param name="dispatcher">Delivers each message.</param>
    /// <param name="options">The processor options. Defaults apply when null.</param>
    /// <param name="timeProvider">The clock deciding eligibility and backoff. Defaults to the system clock.</param>
    /// <param name="logger">Where failures and dead letters are logged.</param>
    public OutboxRunner(
        Func<DbContext> contextFactory,
        IOutboxDispatcher dispatcher,
        OutboxOptions? options = null,
        TimeProvider? timeProvider = null,
        ILogger? logger = null)
        : this(new DelegateOutboxContextSource(Required(contextFactory)), "DbContext", options ?? new OutboxOptions(), timeProvider, logger)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        this.dispatcher = dispatcher;
    }

    /// <summary>Creates a runner that resolves <see cref="IOutboxDispatcher"/> from a fresh DI scope per message.</summary>
    /// <param name="contextFactory">Creates a new context per unit of work. See the other constructor.</param>
    /// <param name="scopeFactory">Scopes the dispatcher is resolved from, one per message.</param>
    /// <param name="options">The processor options. Defaults apply when null.</param>
    /// <param name="timeProvider">The clock deciding eligibility and backoff. Defaults to the system clock.</param>
    /// <param name="logger">Where failures and dead letters are logged.</param>
    public OutboxRunner(
        Func<DbContext> contextFactory,
        IServiceScopeFactory scopeFactory,
        OutboxOptions? options = null,
        TimeProvider? timeProvider = null,
        ILogger? logger = null)
        : this(new DelegateOutboxContextSource(Required(contextFactory)), "DbContext", options ?? new OutboxOptions(), timeProvider, logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        this.scopes = scopeFactory;
    }

    /// <summary>The DI form, used by <c>AddDbContextOutbox</c>: contexts and the keyed dispatcher both come from
    /// scopes of the container.</summary>
    internal OutboxRunner(
        OutboxContextSource contexts,
        string contextName,
        IServiceScopeFactory scopeFactory,
        object dispatcherKey,
        OutboxOptions options,
        TimeProvider? timeProvider,
        ILogger? logger)
        : this(contexts, contextName, options, timeProvider, logger)
    {
        this.scopes = scopeFactory;
        this.dispatcherKey = dispatcherKey;
    }

    OutboxRunner(OutboxContextSource contexts, string contextName, OutboxOptions options, TimeProvider? timeProvider, ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        this.contexts = contexts;
        this.contextName = contextName;
        this.options = options;
        this.clock = timeProvider ?? TimeProvider.System;
        this.logger = logger ?? NullLogger.Instance;
    }

    static Func<DbContext> Required(Func<DbContext> contextFactory)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        return contextFactory;
    }

    /// <summary>The options this runner drains with.</summary>
    public OutboxOptions Options => this.options;

    static IQueryable<OutboxMessage> Messages(DbContext context) => context.Set<OutboxMessage>().AsNoTracking();

    static IQueryable<OutboxMessage> Eligible(DbContext context, DateTimeOffset now)
        => Messages(context).Where(m => m.ProcessedAt == null && m.DeadLetteredAt == null && m.AvailableAt <= now);

    /// <summary>Throws when the context cannot hold an outbox (a non-relational provider, or no
    /// <c>modelBuilder.AddOutbox()</c>). The hosted processor calls this at start-up so a misconfiguration fails the
    /// host by name instead of logging a poll error forever.</summary>
    public async Task EnsureSupported(CancellationToken cancellationToken = default)
    {
        await using var lease = await this.contexts.Rent(cancellationToken).ConfigureAwait(false);
        OutboxGuard.EnsureSupported(lease.Context);
    }

    /// <summary>How many messages are eligible for delivery right now.</summary>
    public async Task<int> PendingDepth(CancellationToken cancellationToken = default)
    {
        await using var lease = await this.contexts.Rent(cancellationToken).ConfigureAwait(false);
        var depth = await Eligible(lease.Context, this.clock.GetUtcNow()).CountAsync(cancellationToken).ConfigureAwait(false);
        OutboxDiagnostics.RecordPending(this.contextName, depth);
        return depth;
    }

    /// <summary>
    /// Claims and dispatches up to <see cref="OutboxOptions.BatchSize"/> messages, and returns how many were
    /// dispatched (successfully or not) by <i>this</i> runner. A message another worker claimed first is not
    /// counted.
    /// </summary>
    public async Task<int> DrainOnce(CancellationToken cancellationToken = default)
    {
        List<OutboxMessage> candidates;
        await using (var lease = await this.contexts.Rent(cancellationToken).ConfigureAwait(false))
        {
            candidates = await Eligible(lease.Context, this.clock.GetUtcNow())
                // v7 ids sort by creation time, so this is FIFO without a second index.
                .OrderBy(m => m.Id)
                .Take(this.options.BatchSize)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        if (candidates.Count == 0)
            return 0;

        // One "chain" is a sequence that must run in order. Unordered mode makes every message its own chain, so
        // both modes are the same fan-out over chains and only the grouping differs.
        var chains = this.options.OrderedPartitions
            ? candidates
                // A message with no partition key has no ordering requirement, so it gets a group of its own rather
                // than sharing one with every other unkeyed message.
                .GroupBy(m => m.PartitionKey ?? " " + m.Id, StringComparer.Ordinal)
                .Select(g => (IReadOnlyList<OutboxMessage>)[.. g])
                .ToList()
            : [.. candidates.Select(m => (IReadOnlyList<OutboxMessage>)[m])];

        using var gate = new SemaphoreSlim(this.options.MaxParallelism);
        var counts = new int[chains.Count];
        await Task.WhenAll(chains.Select(async (chain, index) =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                counts[index] = await this.RunChain(chain, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);

        return counts.Sum();
    }

    async Task<int> RunChain(IReadOnlyList<OutboxMessage> chain, CancellationToken cancellationToken)
    {
        // One context per lane: DbContext is not thread-safe, and a lane runs its messages sequentially.
        await using var lease = await this.contexts.Rent(cancellationToken).ConfigureAwait(false);
        var context = lease.Context;

        var dispatched = 0;
        var index = 0;
        var blocked = false;
        while (index < chain.Count && !blocked && !cancellationToken.IsCancellationRequested)
        {
            var message = chain[index++];
            if (!await this.Claim(context, message, cancellationToken).ConfigureAwait(false))
            {
                // Another worker owns this message. In an ordered partition it also owns everything behind it, so
                // this chain stops here rather than delivering out of order.
                blocked = this.options.OrderedPartitions;
            }
            else
            {
                dispatched++;
                var delivered = await this.Dispatch(context, message, cancellationToken).ConfigureAwait(false);
                // A poison message blocks only its own partition — and only until it dead-letters.
                blocked = !delivered && this.options.OrderedPartitions;
            }
        }
        return dispatched;
    }

    async Task<bool> Claim(DbContext context, OutboxMessage message, CancellationToken cancellationToken)
    {
        var read = message.Version;
        var attempts = message.Attempts + 1;
        var availableAt = this.clock.GetUtcNow() + this.options.Backoff(attempts);

        var claimed = await Messages(context)
            .Where(m => m.Id == message.Id && m.Version == read)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Attempts, attempts)
                .SetProperty(m => m.AvailableAt, availableAt)
                .SetProperty(m => m.Version, read + 1),
                cancellationToken)
            .ConfigureAwait(false);

        if (claimed != 1)
            return false;

        message.Attempts = attempts;
        message.AvailableAt = availableAt;
        message.Version = read + 1;
        return true;
    }

    async Task<bool> Dispatch(DbContext context, OutboxMessage message, CancellationToken cancellationToken)
    {
        using var activity = OutboxDiagnostics.StartDispatch(message);
        var start = Stopwatch.GetTimestamp();

        try
        {
            await this.Deliver(message, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown, not a delivery failure. The claim already pushed AvailableAt out, so the message comes back
            // on its own without recording an error.
            throw;
        }
        catch (Exception ex)
        {
            OutboxDiagnostics.RecordDispatch(message.MessageType, Stopwatch.GetElapsedTime(start), "error", ex.GetType().FullName);
            if (activity is { IsAllDataRequested: true })
            {
                activity.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity.AddException(ex);
            }
            await this.Fail(context, message, ex, cancellationToken).ConfigureAwait(false);
            return false;
        }

        OutboxDiagnostics.RecordDispatch(message.MessageType, Stopwatch.GetElapsedTime(start), "success", null);
        var processedAt = this.clock.GetUtcNow();
        await this.Save(
            context,
            message,
            s => s.SetProperty(m => m.ProcessedAt, processedAt).SetProperty(m => m.Error, (string?)null),
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    // A fresh DI scope per message, so a scoped dispatcher (and everything it injects) behaves the way it would in a
    // request.
    async Task Deliver(OutboxMessage message, CancellationToken cancellationToken)
    {
        if (this.scopes == null)
        {
            await this.dispatcher!.Dispatch(message, cancellationToken).ConfigureAwait(false);
            return;
        }

        await using var scope = this.scopes.CreateAsyncScope();
        var scoped = this.dispatcherKey == null
            ? scope.ServiceProvider.GetRequiredService<IOutboxDispatcher>()
            : scope.ServiceProvider.GetRequiredKeyedService<IOutboxDispatcher>(this.dispatcherKey);
        await scoped.Dispatch(message, cancellationToken).ConfigureAwait(false);
    }

    async Task Fail(DbContext context, OutboxMessage message, Exception error, CancellationToken cancellationToken)
    {
        var description = Describe(error);
        if (message.Attempts >= this.options.MaxAttempts)
        {
            var deadLetteredAt = this.clock.GetUtcNow();
            OutboxDiagnostics.RecordDeadLettered(message.MessageType);
            this.logger.LogError(error, "Outbox message {MessageId} ({MessageType}) dead-lettered after {Attempts} attempts", message.Id, message.MessageType, message.Attempts);
            await this.Save(
                context,
                message,
                s => s.SetProperty(m => m.Error, description).SetProperty(m => m.DeadLetteredAt, deadLetteredAt),
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            this.logger.LogWarning(error, "Outbox message {MessageId} ({MessageType}) attempt {Attempts} failed; retrying after {AvailableAt:O}", message.Id, message.MessageType, message.Attempts, message.AvailableAt);
            await this.Save(context, message, s => s.SetProperty(m => m.Error, description), cancellationToken).ConfigureAwait(false);
        }
    }

    // Conditioned on the version the claim wrote. A conflict means someone changed the message underneath us — an
    // operator requeueing it, most likely. Their write is the newer intent, so it wins and this one is dropped.
    async Task Save(
        DbContext context,
        OutboxMessage message,
        Action<Microsoft.EntityFrameworkCore.Query.UpdateSettersBuilder<OutboxMessage>> set,
        CancellationToken cancellationToken)
    {
        var read = message.Version;
        // Not the dispatch token: the message was delivered (or failed) and that outcome must be recorded even when
        // the host is shutting down, or a delivered message would be delivered again.
        var saved = await Messages(context)
            .Where(m => m.Id == message.Id && m.Version == read)
            .ExecuteUpdateAsync(s =>
            {
                set(s);
                s.SetProperty(m => m.Version, read + 1);
            }, CancellationToken.None)
            .ConfigureAwait(false);

        if (saved == 1)
            message.Version = read + 1;
        else
            this.logger.LogInformation("Outbox message {MessageId} changed during dispatch; the concurrent write wins", message.Id);
    }

    /// <summary>
    /// Deletes acknowledged messages older than <see cref="OutboxOptions.Retention"/>, and returns how many went.
    /// A null retention keeps everything, as an audit trail, and this is a no-op.
    /// </summary>
    public async Task<int> PurgeExpired(CancellationToken cancellationToken = default)
    {
        if (this.options.Retention is not { } retention)
            return 0;

        var cutoff = this.clock.GetUtcNow() - retention;
        await using var lease = await this.contexts.Rent(cancellationToken).ConfigureAwait(false);
        return await Messages(lease.Context)
            .Where(m => m.ProcessedAt != null && m.ProcessedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    // Enough to identify the failure in an admin grid without storing a page of stack trace per row.
    const int MaxErrorLength = 4000;

    static string Describe(Exception ex)
    {
        var text = $"{ex.GetType().Name}: {ex.Message}";
        return text.Length <= MaxErrorLength ? text : text[..MaxErrorLength];
    }
}
