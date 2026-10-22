using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Shiny.EntityFrameworkCore.Outbox;

/// <summary>
/// The outbox's telemetry: a process-wide <see cref="ActivitySource"/> and <see cref="Meter"/>, both named
/// <see cref="Name"/>. Zero-cost until something listens — subscribe from OpenTelemetry with
/// <c>.AddSource(OutboxDiagnostics.Name)</c> and <c>.AddMeter(OutboxDiagnostics.Name)</c>.
/// </summary>
public static class OutboxDiagnostics
{
    /// <summary>The <see cref="ActivitySource"/> and <see cref="Meter"/> name.</summary>
    public const string Name = "Shiny.EntityFrameworkCore.Outbox";

    /// <summary>The span name for one dispatch attempt.</summary>
    public const string DispatchActivityName = "outbox.dispatch";

    static readonly ActivitySource Source = new(Name);
    static readonly Meter Meter = new(Name);

    // An outbox fails operationally rather than in the data, so it gets its own signals: how many got out, how many
    // gave up, how long delivery takes, and — the one worth alerting on — how deep the queue is right now.
    static readonly Counter<long> Dispatched = Meter.CreateCounter<long>(
        "outbox.dispatched", "{message}", "Outbox messages delivered successfully.");
    static readonly Counter<long> DeadLettered = Meter.CreateCounter<long>(
        "outbox.dead_lettered", "{message}", "Outbox messages that exhausted their delivery attempts.");
    static readonly Histogram<double> DispatchDuration = Meter.CreateHistogram<double>(
        "outbox.dispatch.duration", "s", "Duration of one outbox dispatch attempt.");

    // Written by each processor after its poll, read by the gauge callback (which cannot await, so it reports the
    // depth the processor already measured rather than querying). One series per context type.
    static readonly ConcurrentDictionary<string, long> PendingByContext = new(StringComparer.Ordinal);
    static readonly ObservableGauge<long> Pending = Meter.CreateObservableGauge(
        "outbox.pending", ObservePending, "{message}", "Outbox messages eligible for delivery, as of the processor's latest poll.");

    static IEnumerable<Measurement<long>> ObservePending()
        => PendingByContext.Select(p => new Measurement<long>(p.Value, new KeyValuePair<string, object?>("db.context", p.Key)));

    internal static void RecordPending(string context, long pending) => PendingByContext[context] = pending;

    /// <summary>Starts the span for one dispatch attempt, parented to the trace that enqueued the message.</summary>
    internal static Activity? StartDispatch(OutboxMessage message)
    {
        var parent = default(ActivityContext);
        var hasParent = message.Headers != null
            && message.Headers.TryGetValue(OutboxColumns.TraceParentHeader, out var traceParent)
            && ActivityContext.TryParse(traceParent, null, out parent);

        // The enqueueing request is long finished, so it is linked as well as parented: a backend that renders
        // links but discards stale parents still connects the two.
        var activity = hasParent
            ? Source.StartActivity(DispatchActivityName, ActivityKind.Producer, parent, links: [new ActivityLink(parent)])
            : Source.StartActivity(DispatchActivityName, ActivityKind.Producer);

        if (activity is { IsAllDataRequested: true })
        {
            activity.SetTag("messaging.operation.name", "publish");
            activity.SetTag("messaging.message.id", message.Id);
            activity.SetTag("messaging.message.type", message.MessageType);
            activity.SetTag("messaging.outbox.attempt", message.Attempts);
            if (message.PartitionKey != null)
                activity.SetTag("messaging.destination.partition.id", message.PartitionKey);
        }
        return activity;
    }

    internal static void RecordDispatch(string messageType, TimeSpan elapsed, string outcome, string? errorType)
    {
        var tags = new TagList
        {
            { "messaging.message.type", messageType },
            { "outcome", outcome }
        };
        if (errorType != null)
            tags.Add("error.type", errorType);

        DispatchDuration.Record(elapsed.TotalSeconds, tags);
        if (outcome == "success")
            Dispatched.Add(1, tags);
    }

    internal static void RecordDeadLettered(string messageType)
        => DeadLettered.Add(1, new TagList { { "messaging.message.type", messageType } });
}
