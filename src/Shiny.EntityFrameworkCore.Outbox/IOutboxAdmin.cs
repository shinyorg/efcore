namespace Shiny.EntityFrameworkCore.Outbox;

/// <summary>
/// The in-process operational view of the outbox: how deep is the queue, what is dead-lettered, put it back,
/// clean it up. Registered by <c>AddDbContextOutbox</c> — keyed by the context type, and unkeyed for the first
/// context registered.
/// </summary>
/// <remarks>Use it for health checks and dashboards.</remarks>
public interface IOutboxAdmin
{
    /// <summary>Undelivered messages that are eligible right now — the queue depth to alert on.</summary>
    Task<int> PendingCount(CancellationToken cancellationToken = default);

    /// <summary>Undelivered messages still waiting out a backoff (or a claim's visibility timeout).</summary>
    Task<int> ScheduledCount(CancellationToken cancellationToken = default);

    /// <summary>How many messages are dead-lettered.</summary>
    Task<int> DeadLetterCount(CancellationToken cancellationToken = default);

    /// <summary>
    /// When the oldest eligible message was enqueued, or null when nothing is pending. This — not the depth —
    /// is what separates "busy" from "the processor is dead".
    /// </summary>
    Task<DateTimeOffset?> OldestPendingAt(CancellationToken cancellationToken = default);

    /// <summary>The dead-lettered messages, newest first.</summary>
    Task<IReadOnlyList<OutboxMessage>> DeadLetters(int take = 100, CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes dead-lettered messages eligible again: clears the dead-letter state and the error, zeroes the
    /// attempt counter and sets <c>AvailableAt</c> to now. Messages that are merely scheduled, or already
    /// processed, are skipped even when named explicitly — resetting a backoff, or redelivering a business
    /// event that already happened, are both worse than doing nothing. Returns how many were requeued.
    /// </summary>
    Task<int> Requeue(IEnumerable<string> ids, CancellationToken cancellationToken = default);

    /// <summary>Requeues every dead letter, optionally narrowed to one message type. Returns how many were requeued.</summary>
    Task<int> RequeueAllDeadLettered(string? messageType = null, CancellationToken cancellationToken = default);

    /// <summary>Deletes acknowledged messages older than <paramref name="olderThan"/>. Pending and dead-lettered
    /// messages are never touched. Returns how many were deleted.</summary>
    Task<int> PurgeProcessed(DateTimeOffset olderThan, CancellationToken cancellationToken = default);
}
