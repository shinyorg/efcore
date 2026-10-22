using System.Text.Json;

namespace Shiny.EntityFrameworkCore.Outbox;

/// <summary>Which bucket an <see cref="OutboxMessage"/> is in, derived from its three nullable timestamps.</summary>
[Flags]
public enum OutboxStates
{
    /// <summary>Undelivered and eligible now.</summary>
    Pending = 1,

    /// <summary>Undelivered, waiting out a backoff — <c>AvailableAt</c> is still in the future.</summary>
    Scheduled = 2,

    /// <summary>Out of attempts. Never deleted automatically; requeue or purge it.</summary>
    DeadLettered = 4,

    /// <summary>Delivered and acknowledged.</summary>
    Processed = 8,

    All = Pending | Scheduled | DeadLettered | Processed
}

/// <summary>Shapes the read-only stream returned by <c>WatchOutbox(…)</c>.</summary>
public sealed class OutboxWatchOptions
{
    /// <summary>Which states to yield. <see cref="OutboxStates.Pending"/> is the interesting one;
    /// <see cref="OutboxStates.DeadLettered"/> is the one worth alerting on.</summary>
    public OutboxStates States { get; set; } = OutboxStates.Pending;

    /// <summary>Restricts the stream to one ordering scope.</summary>
    public string? PartitionKey { get; set; }

    /// <summary>Restricts the stream to one message type.</summary>
    public string? MessageType { get; set; }

    /// <summary>How often the table is read. Defaults to 5 seconds.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Yields what is already queued before tailing. On by default — a dashboard that starts mid-backlog and shows
    /// nothing is a bug, not a feature.
    /// </summary>
    public bool IncludeExisting { get; set; } = true;

    /// <summary>How many rows one poll reads per state. Defaults to 100.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>The clock that decides Pending from Scheduled and paces the poll. Defaults to the system clock.</summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>Serializer options for the payload, used by the decoding <c>WatchOutbox&lt;TEvent&gt;</c> overload.
    /// Defaults to camelCase, matching <see cref="OutboxOptions.PayloadSerializerOptions"/>.</summary>
    public JsonSerializerOptions PayloadSerializerOptions { get; set; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    internal void Validate()
    {
        if ((this.States & OutboxStates.All) == 0)
            throw new ArgumentException("OutboxWatchOptions.States must name at least one state to watch.", nameof(this.States));
        if (this.PollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(this.PollInterval), this.PollInterval, "OutboxWatchOptions.PollInterval must be greater than zero.");
        if (this.BatchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(this.BatchSize), this.BatchSize, "OutboxWatchOptions.BatchSize must be greater than zero.");
    }
}

/// <summary>A watched message with its payload already deserialized.</summary>
public sealed record OutboxEnvelope<TEvent>(OutboxMessage Message, TEvent Event) where TEvent : class;
