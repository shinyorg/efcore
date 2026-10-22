namespace Shiny.EntityFrameworkCore.Outbox;

/// <summary>
/// A pending domain event — an ordinary entity in your model, so it is created by your migrations, committed by
/// your <c>SaveChanges</c>, and queryable like anything else in the context.
/// </summary>
/// <remarks>
/// <para>
/// Every column name is pinned by <see cref="OutboxModelBuilderExtensions.AddOutbox"/> (see
/// <see cref="OutboxColumns"/>). That is part of this type's contract, not a style choice: outbox rows are read by
/// <b>other processes</b> — an admin screen, a relay written in another language, a hand-written query during an
/// incident — so the stored shape must not follow whatever naming convention the owning application happens to
/// configure.
/// </para>
/// <para>
/// Delivery is <b>at-least-once</b>. A dispatcher that succeeds and then crashes before its acknowledgement is
/// written will deliver the same message again, so consumers must be idempotent.
/// </para>
/// </remarks>
public sealed class OutboxMessage
{
    /// <summary>A monotonic version-7 (time-ordered) UUID in "N" format, so ordering by id is FIFO without a
    /// second index.</summary>
    public string Id { get; set; } = null!;

    /// <summary>The logical event name a dispatcher switches on — the CLR type's full name by default.</summary>
    public required string MessageType { get; set; }

    /// <summary>The serialized event body.</summary>
    public required string Payload { get; set; }

    /// <summary>The ordering scope. Null means the message has no ordering requirement.</summary>
    public string? PartitionKey { get; set; }

    /// <summary>Arbitrary transport metadata, including the <c>traceparent</c> captured at enqueue. Stored as a
    /// JSON object in one column.</summary>
    public Dictionary<string, string>? Headers { get; set; }

    /// <summary>When the message was enqueued.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The backoff gate — the processor ignores the message until this moment passes.</summary>
    public DateTimeOffset AvailableAt { get; set; }

    /// <summary>How many times delivery has been attempted.</summary>
    public int Attempts { get; set; }

    /// <summary>When the message was successfully delivered. Null while it is still in flight.</summary>
    public DateTimeOffset? ProcessedAt { get; set; }

    /// <summary>When the message exhausted its attempts. Dead letters are never deleted automatically — requeue
    /// or purge them.</summary>
    public DateTimeOffset? DeadLetteredAt { get; set; }

    /// <summary>The last dispatch failure.</summary>
    public string? Error { get; set; }

    /// <summary>
    /// The optimistic-concurrency counter — and the claim primitive. Every write the processor makes is
    /// conditioned on the version it read and bumps it, so two processors that read the same message cannot both
    /// claim it. Mapped as a concurrency token, so your own <c>SaveChanges</c> edits of a message get the same
    /// protection.
    /// </summary>
    public int Version { get; set; }
}

/// <summary>
/// The pinned column names of <see cref="OutboxMessage"/>. Anything addressing outbox rows without the CLR type —
/// raw SQL, a relay in another process, a dashboard — uses these. They are lower-case snake_case so they need no
/// quoting on any provider (PostgreSQL folds unquoted identifiers to lower case).
/// </summary>
public static class OutboxColumns
{
    public const string Id = "id";
    public const string MessageType = "message_type";
    public const string Payload = "payload";
    public const string PartitionKey = "partition_key";
    public const string Headers = "headers";
    public const string CreatedAt = "created_at";
    public const string AvailableAt = "available_at";
    public const string Attempts = "attempts";
    public const string ProcessedAt = "processed_at";
    public const string DeadLetteredAt = "dead_lettered_at";
    public const string Error = "error";
    public const string Version = "version";

    /// <summary>Every pinned name, for the guard test and for schema-free readers that enumerate them.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Id, MessageType, Payload, PartitionKey, Headers,
        CreatedAt, AvailableAt, Attempts, ProcessedAt, DeadLetteredAt, Error, Version
    ];

    /// <summary>The header key carrying the W3C trace context captured when the message was enqueued.</summary>
    public const string TraceParentHeader = "traceparent";
}
