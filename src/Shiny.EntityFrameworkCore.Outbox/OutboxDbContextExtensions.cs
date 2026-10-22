using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.EntityFrameworkCore;

namespace Shiny.EntityFrameworkCore.Outbox;

/// <summary>
/// The transactional half of the outbox: enqueue adds the message to the change tracker, so it commits in the
/// same <c>SaveChanges</c> transaction as your entities — together or not at all.
/// </summary>
public static class OutboxDbContextExtensions
{
    /// <summary>
    /// Adds a domain event to this context as an <see cref="OutboxMessage"/>. Nothing is written until
    /// <c>SaveChanges</c>, which inserts the message in the <b>same transaction</b> as everything else the context
    /// is tracking — that atomicity is the entire point, so never save the message through a different context.
    /// <code>
    /// db.Orders.Add(order);
    /// db.Enqueue(new OrderPlaced(order.Id, order.Total), partitionKey: order.Id.ToString());
    /// await db.SaveChangesAsync();
    /// </code>
    /// </summary>
    /// <param name="context">The context doing the business write.</param>
    /// <param name="evt">The event. Serialized into <see cref="OutboxMessage.Payload"/> with
    /// <see cref="OutboxOptions.PayloadSerializerOptions"/>.</param>
    /// <param name="partitionKey">The ordering scope, honoured when <see cref="OutboxOptions.OrderedPartitions"/> is on.</param>
    /// <param name="headers">Extra transport metadata. <c>traceparent</c> is added automatically.</param>
    /// <returns>The tracked message (state <c>Added</c>).</returns>
    [RequiresUnreferencedCode("Serializes the event with reflection. Use the JsonTypeInfo<TEvent> overload in a trimmed or AOT application.")]
    [RequiresDynamicCode("Serializes the event with reflection. Use the JsonTypeInfo<TEvent> overload in a trimmed or AOT application.")]
    public static OutboxMessage Enqueue<TEvent>(
        this DbContext context,
        TEvent evt,
        string? partitionKey = null,
        IDictionary<string, string>? headers = null) where TEvent : class
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(evt);

        var options = OutboxGuard.ResolveOptions(context);
        var payload = JsonSerializer.Serialize(evt, options.PayloadSerializerOptions);
        return context.EnqueueRaw(MessageTypeOf(typeof(TEvent)), payload, partitionKey, headers);
    }

    /// <summary>AOT-safe overload — serializes the event through supplied metadata rather than reflection.</summary>
    public static OutboxMessage Enqueue<TEvent>(
        this DbContext context,
        TEvent evt,
        JsonTypeInfo<TEvent> jsonTypeInfo,
        string? partitionKey = null,
        IDictionary<string, string>? headers = null) where TEvent : class
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(evt);
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);

        var payload = JsonSerializer.Serialize(evt, jsonTypeInfo);
        return context.EnqueueRaw(MessageTypeOf(typeof(TEvent)), payload, partitionKey, headers);
    }

    /// <summary>
    /// Enqueues a pre-serialized event — the lane for a payload you already hold as JSON, or a message type that is
    /// a contract name rather than a CLR type. Spelled differently from <c>Enqueue</c> on purpose:
    /// <c>Enqueue("OrderPlaced", json)</c> would otherwise be a legal — and silently wrong — call to the generic
    /// overload with <c>TEvent</c> inferred as <see cref="string"/>.
    /// </summary>
    public static OutboxMessage EnqueueRaw(
        this DbContext context,
        string messageType,
        string payload,
        string? partitionKey = null,
        IDictionary<string, string>? headers = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageType);
        ArgumentNullException.ThrowIfNull(payload);
        OutboxGuard.EnsureSupported(context);

        var message = Create(OutboxId.Next(), messageType, payload, partitionKey, headers, OutboxGuard.ResolveClock(context).GetUtcNow());
        context.Set<OutboxMessage>().Add(message);
        return message;
    }

    /// <summary>The logical message type recorded for a CLR event type.</summary>
    internal static string MessageTypeOf(Type type) => type.FullName ?? type.Name;

    internal static OutboxMessage Create(
        string id,
        string messageType,
        string payload,
        string? partitionKey,
        IDictionary<string, string>? headers,
        DateTimeOffset now) => new()
    {
        // Time-ordered and monotonic within the process, so the processor reads FIFO with OrderBy(Id) and needs no
        // second index. See OutboxId for why Guid.CreateVersion7 is not enough on its own.
        Id = id,
        MessageType = messageType,
        Payload = payload,
        PartitionKey = partitionKey,
        Headers = BuildHeaders(headers),
        CreatedAt = now,
        AvailableAt = now
    };

    // The event's causing request, so the dispatch span joins its trace. Captured here rather than at dispatch
    // because by then the originating activity is long gone.
    static Dictionary<string, string>? BuildHeaders(IDictionary<string, string>? supplied)
    {
        var traceParent = Activity.Current?.Id;
        if (supplied == null && traceParent == null)
            return null;

        var headers = supplied == null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(supplied, StringComparer.Ordinal);

        if (traceParent != null && !headers.ContainsKey(OutboxColumns.TraceParentHeader))
            headers[OutboxColumns.TraceParentHeader] = traceParent;

        return headers;
    }
}
