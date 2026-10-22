using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.EntityFrameworkCore;

namespace Shiny.EntityFrameworkCore.Outbox;

/// <summary>
/// The observation half of the outbox — one <c>await foreach</c> for a dashboard, a health check, or a test.
/// </summary>
public static class OutboxWatchExtensions
{
    /// <summary>
    /// Streams outbox message revisions as they appear. <b>Read-only</b>: it never claims, acknowledges or mutates a
    /// message, so it cannot compete with the processor for work.
    /// <code>
    /// await foreach (var msg in db.WatchOutbox(o => o.States = OutboxStates.DeadLettered, ct))
    ///     logger.LogError("Outbox dead letter {Id}: {Error}", msg.Id, msg.Error);
    /// </code>
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a <b>poll</b>, and that is the right shape rather than a compromise: a failed message becomes pending
    /// again purely because its <c>AvailableAt</c> elapsed — a clock event with no write behind it — and an outbox
    /// usually has writers in several processes, so no change notification could yield every transition anyway.
    /// </para>
    /// <para>
    /// It deliberately does <b>not</b> promise every revision: a message enqueued, dispatched and acknowledged between
    /// two polls is never observed, which is correct for a monitor. If you need to see every message, you are the
    /// dispatcher — register one with <c>AddDbContextOutbox</c> rather than watching, or you will have built a second,
    /// competing consumer and duplicated a side effect in production.
    /// </para>
    /// <para>
    /// Each revision is yielded once, keyed on <see cref="OutboxMessage.Version"/>, which every outbox write moves. A
    /// delivery attempt is two revisions — the claim (<see cref="OutboxMessage.Attempts"/> bumped,
    /// <see cref="OutboxMessage.AvailableAt"/> pushed out by the backoff) and then its outcome.
    /// </para>
    /// <para>
    /// The stream reads through <paramref name="context"/> for as long as it runs (untracked queries, one at a time),
    /// so give it a context that nothing else is using concurrently.
    /// </para>
    /// </remarks>
    public static IAsyncEnumerable<OutboxMessage> WatchOutbox(
        this DbContext context,
        Action<OutboxWatchOptions>? configure = null,
        CancellationToken cancellationToken = default)
    {
        var options = Prepare(context, configure);
        return Watch(context, options, cancellationToken);
    }

    /// <summary>
    /// As <see cref="WatchOutbox(DbContext, Action{OutboxWatchOptions}, CancellationToken)"/>, narrowed to one event
    /// type (its full CLR name, unless <see cref="OutboxWatchOptions.MessageType"/> says otherwise) and with the
    /// payload deserialized.
    /// </summary>
    [RequiresUnreferencedCode("Deserializes the payload with reflection. Use the JsonTypeInfo overload in a trimmed or AOT application.")]
    [RequiresDynamicCode("Deserializes the payload with reflection. Use the JsonTypeInfo overload in a trimmed or AOT application.")]
    public static IAsyncEnumerable<OutboxEnvelope<TEvent>> WatchOutbox<TEvent>(
        this DbContext context,
        Action<OutboxWatchOptions>? configure = null,
        CancellationToken cancellationToken = default) where TEvent : class
    {
        var options = Prepare(context, configure);
        options.MessageType ??= OutboxDbContextExtensions.MessageTypeOf(typeof(TEvent));
        return Decode(Watch(context, options, cancellationToken), payload => JsonSerializer.Deserialize<TEvent>(payload, options.PayloadSerializerOptions), cancellationToken);
    }

    /// <summary>AOT-safe overload — deserializes the payload through supplied metadata rather than reflection.</summary>
    public static IAsyncEnumerable<OutboxEnvelope<TEvent>> WatchOutbox<TEvent>(
        this DbContext context,
        JsonTypeInfo<TEvent> jsonTypeInfo,
        Action<OutboxWatchOptions>? configure = null,
        CancellationToken cancellationToken = default) where TEvent : class
    {
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);
        var options = Prepare(context, configure);
        options.MessageType ??= OutboxDbContextExtensions.MessageTypeOf(typeof(TEvent));
        return Decode(Watch(context, options, cancellationToken), payload => JsonSerializer.Deserialize(payload, jsonTypeInfo), cancellationToken);
    }

    // Validated eagerly, so a bad configuration throws at the call rather than on the first MoveNextAsync.
    static OutboxWatchOptions Prepare(DbContext context, Action<OutboxWatchOptions>? configure)
    {
        ArgumentNullException.ThrowIfNull(context);
        OutboxGuard.EnsureSupported(context);

        var options = new OutboxWatchOptions();
        configure?.Invoke(options);
        options.Validate();
        return options;
    }

    static async IAsyncEnumerable<OutboxEnvelope<TEvent>> Decode<TEvent>(
        IAsyncEnumerable<OutboxMessage> messages,
        Func<string, TEvent?> deserialize,
        [EnumeratorCancellation] CancellationToken cancellationToken) where TEvent : class
    {
        await foreach (var message in messages.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (deserialize(message.Payload) is { } evt)
                yield return new OutboxEnvelope<TEvent>(message, evt);
        }
    }

    static async IAsyncEnumerable<OutboxMessage> Watch(
        DbContext context,
        OutboxWatchOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Dedupe on (Id, Version), never on Id alone: a message legitimately reappears — a retry bumps Attempts, a
        // requeue clears the dead-letter state — and each of those is a revision worth yielding. An id watermark
        // would be wrong twice over: requeue walks a message backwards through the states, and v7 ordering says
        // nothing about revisions.
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var previousIds = new HashSet<string>(StringComparer.Ordinal);
        var yieldBatch = options.IncludeExisting;

        var running = true;
        while (running && !cancellationToken.IsCancellationRequested)
        {
            IReadOnlyList<OutboxMessage>? batch = null;
            try
            {
                batch = await Read(context, options, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Cancelling mid-poll ends the stream rather than throwing out of the caller's await foreach: the
                // token is how you stop a monitor, not an error condition.
                running = false;
            }

            if (batch != null)
            {
                var currentIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var message in batch)
                {
                    currentIds.Add(message.Id);
                    var isNewRevision = !seen.TryGetValue(message.Id, out var version) || version != message.Version;
                    seen[message.Id] = message.Version;

                    if (isNewRevision && yieldBatch)
                        yield return message;
                }

                // Two generations of ids are retained, so the dedupe set stays bounded on a long-lived watch without a
                // message that merely paged out of one poll re-yielding on the next.
                foreach (var stale in seen.Keys.Where(k => !currentIds.Contains(k) && !previousIds.Contains(k)).ToList())
                    seen.Remove(stale);

                previousIds = currentIds;
                // IncludeExisting is nothing more than whether the first poll yields its results or only records them.
                yieldBatch = true;

                running = await Wait(options, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    static async Task<bool> Wait(OutboxWatchOptions options, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(options.PollInterval, options.TimeProvider, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    // States are OR-ed by running one query per requested state and merging: at most four cheap, index-friendly
    // predicates rather than one OR of null checks.
    static async Task<IReadOnlyList<OutboxMessage>> Read(DbContext context, OutboxWatchOptions options, CancellationToken cancellationToken)
    {
        var now = options.TimeProvider.GetUtcNow();
        var merged = new Dictionary<string, OutboxMessage>(StringComparer.Ordinal);

        foreach (var state in Enum.GetValues<OutboxStates>().Where(s => s != OutboxStates.All && options.States.HasFlag(s)))
        {
            var query = context.Set<OutboxMessage>().AsNoTracking();
            query = state switch
            {
                OutboxStates.Pending => query.Where(m => m.ProcessedAt == null && m.DeadLetteredAt == null && m.AvailableAt <= now),
                OutboxStates.Scheduled => query.Where(m => m.ProcessedAt == null && m.DeadLetteredAt == null && m.AvailableAt > now),
                OutboxStates.DeadLettered => query.Where(m => m.DeadLetteredAt != null),
                _ => query.Where(m => m.ProcessedAt != null)
            };

            if (options.PartitionKey is { } partitionKey)
                query = query.Where(m => m.PartitionKey == partitionKey);
            if (options.MessageType is { } messageType)
                query = query.Where(m => m.MessageType == messageType);

            var rows = await query
                .OrderBy(m => m.Id)
                .Take(options.BatchSize)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var row in rows)
                merged[row.Id] = row;
        }

        return [.. merged.Values.OrderBy(m => m.Id, StringComparer.Ordinal)];
    }
}
