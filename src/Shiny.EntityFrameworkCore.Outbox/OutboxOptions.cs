using System.Text.Json;

namespace Shiny.EntityFrameworkCore.Outbox;

/// <summary>How the outbox serializes events and drains the queue. Configured on
/// <c>services.AddDbContextOutbox&lt;TContext, TDispatcher&gt;(…)</c>, one instance per context type.</summary>
/// <remarks>
/// The table the messages live in is <b>not</b> here — that is a model mapping, chosen by
/// <c>modelBuilder.AddOutbox(tableName)</c>, so there is exactly one place it can be set, your migrations see it,
/// and the two halves cannot disagree.
/// </remarks>
public sealed class OutboxOptions
{
    /// <summary>How many messages one poll claims. Defaults to 50.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>How many messages are dispatched concurrently. Defaults to 4. Each concurrent lane uses its own
    /// <c>DbContext</c>, so this is also the peak number of contexts (and pooled connections) a pass holds.</summary>
    public int MaxParallelism { get; set; } = 4;

    /// <summary>Attempts before a message is dead-lettered. Defaults to 8.</summary>
    public int MaxAttempts { get; set; } = 8;

    /// <summary>How often the queue is polled when it is empty. Defaults to 5 seconds.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The delay before attempt <c>n</c> may be retried. Doubles as the claim's visibility timeout: claiming a
    /// message pushes <see cref="OutboxMessage.AvailableAt"/> out by this much, so a processor that dies
    /// mid-dispatch releases the message rather than stranding it. Make it longer than your slowest dispatch.
    /// </summary>
    public Func<int, TimeSpan> Backoff { get; set; } = attempt => TimeSpan.FromSeconds(Math.Pow(2, Math.Min(attempt, 16)));

    /// <summary>Deletes acknowledged messages older than this. Null keeps them forever, as an audit trail.</summary>
    public TimeSpan? Retention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>How often the retention sweep runs. Defaults to 5 minutes; ignored when <see cref="Retention"/> is null.</summary>
    public TimeSpan RetentionInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Dispatches messages sharing a <see cref="OutboxMessage.PartitionKey"/> strictly in order, one at a time.
    /// Different partitions still run in parallel, and a message with no partition key is unordered.
    /// </summary>
    public bool OrderedPartitions { get; set; }

    /// <summary>
    /// Serializer options for the <b>event payload</b>. Defaults to camelCase. Used by <c>Enqueue</c> and by the
    /// declarative <c>AddOutboxPublishing</c> interceptor of the same context type.
    /// </summary>
    public JsonSerializerOptions PayloadSerializerOptions { get; set; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    internal void Validate()
    {
        if (this.BatchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(this.BatchSize), this.BatchSize, "OutboxOptions.BatchSize must be greater than zero.");
        if (this.MaxParallelism <= 0)
            throw new ArgumentOutOfRangeException(nameof(this.MaxParallelism), this.MaxParallelism, "OutboxOptions.MaxParallelism must be greater than zero.");
        if (this.MaxAttempts <= 0)
            throw new ArgumentOutOfRangeException(nameof(this.MaxAttempts), this.MaxAttempts, "OutboxOptions.MaxAttempts must be greater than zero.");
        if (this.PollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(this.PollInterval), this.PollInterval, "OutboxOptions.PollInterval must be greater than zero.");
        if (this.Retention is { } retention && retention <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(this.Retention), retention, "OutboxOptions.Retention must be greater than zero, or null to keep processed messages forever.");
        if (this.RetentionInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(this.RetentionInterval), this.RetentionInterval, "OutboxOptions.RetentionInterval must be greater than zero.");
        if (this.Backoff == null)
            throw new ArgumentNullException(nameof(this.Backoff), "OutboxOptions.Backoff is required — it is also the claim's visibility timeout.");
        if (this.PayloadSerializerOptions == null)
            throw new ArgumentNullException(nameof(this.PayloadSerializerOptions), "OutboxOptions.PayloadSerializerOptions is required.");
    }
}

/// <summary>
/// Which change-tracker operations on a mapped entity publish an event, for
/// <c>AddOutboxPublishing(p =&gt; p.Publish&lt;T&gt;(…))</c>.
/// </summary>
[Flags]
public enum OutboxOperations
{
    None = 0,

    /// <summary>The entity was <c>Added</c>.</summary>
    Insert = 1,

    /// <summary>The entity was <c>Modified</c>.</summary>
    Update = 2,

    /// <summary>The entity was <c>Deleted</c>.</summary>
    Delete = 4,

    All = Insert | Update | Delete
}
