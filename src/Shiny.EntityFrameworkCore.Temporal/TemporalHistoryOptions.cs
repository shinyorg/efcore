using System.Text.Json;

namespace Shiny.EntityFrameworkCore.Temporal;

/// <summary>
/// Context-wide history settings passed to
/// <see cref="TemporalDbContextOptionsBuilderExtensions.UseEntityHistory(Microsoft.EntityFrameworkCore.DbContextOptionsBuilder, Action{TemporalHistoryOptions}?)"/>.
/// Which entities keep history is decided in the model with <c>HasHistory()</c>; this class carries the
/// behavior that needs code (actor capture, the clock, snapshot serialization).
/// </summary>
public sealed class TemporalHistoryOptions
{
    /// <summary>
    /// Invoked once per <c>SaveChanges</c> that records history to capture "who" made the change. The value is
    /// stored on every history row written by that save and surfaced as <see cref="EntityVersion{T}.Actor"/>.
    /// </summary>
    public Func<string?>? CaptureActor { get; set; }

    /// <summary>
    /// Scope-aware alternative to <see cref="CaptureActor"/>: invoked with the <b>application service provider</b>
    /// the context was resolved from, so a request-scoped <c>ICurrentUser</c> can supply the actor. With
    /// <c>AddDbContext</c> (scoped options, the default) this is the request scope; with <c>AddDbContextPool</c>
    /// or a pooled factory the options are singletons, so it is the root provider and scoped services are not
    /// reachable. Takes precedence over <see cref="CaptureActor"/> whenever the context has an application
    /// service provider; falls back to <see cref="CaptureActor"/> otherwise.
    /// </summary>
    public Func<IServiceProvider, string?>? ResolveActor { get; set; }

    /// <summary>
    /// The clock used for <see cref="EntityHistoryRecord.ValidFrom"/> / <see cref="EntityHistoryRecord.ValidTo"/>.
    /// Defaults to <see cref="System.TimeProvider.System"/>; inject a fake one to make history deterministic in tests.
    /// </summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>
    /// Serializer options for scalar values inside snapshots. Use it to register converters for property types
    /// System.Text.Json cannot handle out of the box. Defaults to <see cref="JsonSerializerDefaults.General"/>.
    /// </summary>
    public JsonSerializerOptions SerializerOptions { get; set; } = new(JsonSerializerDefaults.General);

    /// <summary>
    /// <c>ExecuteUpdate</c> / <c>ExecuteDelete</c> bypass the change tracker, so they can't be recorded. By default
    /// they throw when they target an entity with history, so history can't silently drift from the table. Set this
    /// to true to allow them anyway (for example a deliberate purge), accepting that they leave no versions behind.
    /// </summary>
    public bool AllowBulkWrites { get; set; }

    internal TemporalHistoryOptions Clone() => new()
    {
        CaptureActor = this.CaptureActor,
        ResolveActor = this.ResolveActor,
        TimeProvider = this.TimeProvider,
        SerializerOptions = this.SerializerOptions,
        AllowBulkWrites = this.AllowBulkWrites
    };
}
