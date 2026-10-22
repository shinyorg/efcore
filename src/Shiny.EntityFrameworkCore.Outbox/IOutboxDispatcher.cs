namespace Shiny.EntityFrameworkCore.Outbox;

/// <summary>
/// Delivers an outbox message to wherever it is going — a bus, an HTTP endpoint, an in-process mediator.
/// This is the seam the library deliberately does not fill: the outbox owns the transactional record and the
/// retry/dead-letter machinery, you own the transport.
/// </summary>
/// <remarks>
/// Resolved from a <b>fresh DI scope per message</b>, so scoped dependencies (including another
/// <c>DbContext</c>) work normally. Register it with
/// <c>services.AddDbContextOutbox&lt;AppDbContext, MyDispatcher&gt;()</c>, or skip the type entirely and pass a
/// delegate to <c>AddDbContextOutbox&lt;AppDbContext&gt;(dispatch)</c>.
/// </remarks>
public interface IOutboxDispatcher
{
    /// <summary>
    /// Delivers one message. Return normally to acknowledge it; throw to fail the attempt, which records the
    /// error, applies the backoff, and eventually dead-letters the message.
    /// </summary>
    Task Dispatch(OutboxMessage message, CancellationToken cancellationToken);
}

/// <summary>Adapts an <c>AddDbContextOutbox(dispatch)</c> delegate to <see cref="IOutboxDispatcher"/>.</summary>
sealed class DelegateOutboxDispatcher(Func<OutboxMessage, CancellationToken, Task> dispatch) : IOutboxDispatcher
{
    public Task Dispatch(OutboxMessage message, CancellationToken cancellationToken)
        => dispatch(message, cancellationToken);
}
