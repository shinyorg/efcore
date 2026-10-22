using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace Shiny.EntityFrameworkCore.Outbox;

/// <summary>
/// Turns <c>Publish&lt;T&gt;</c> registrations into outbox messages inside the <c>SaveChanges</c> that wrote the
/// entities.
/// </summary>
/// <remarks>
/// <para>
/// Most events are built in <c>SavingChanges</c> and simply ride the same save — one round trip, EF's own
/// transaction. The exception is an insert whose key (or any other value) is still a temporary placeholder: the
/// database assigns it during the save, so that event is built in <c>SavedChanges</c> and written by a second
/// <c>SaveChanges</c> pass. To keep that second write atomic with the first, the interceptor begins a transaction
/// in <c>SavingChanges</c> when none is active, and commits it once the messages are in.
/// </para>
/// <para>
/// One instance serves every context built from the options, so per-save state lives in a weak table keyed by the
/// context instance and is removed when the save completes or fails — safe with context pooling.
/// </para>
/// </remarks>
sealed class OutboxPublishingInterceptor(IReadOnlyList<IOutboxPublication> publications) : ISaveChangesInterceptor
{
    readonly ConditionalWeakTable<DbContext, SaveState> states = new();

    sealed class SaveState
    {
        public readonly List<OutboxMessage> Added = [];
        public readonly List<Deferred> Deferred = [];
        public IDbContextTransaction? Owned;
        public bool NeedsTransaction;
        public bool Flushing;
        public OutboxOptions Options = null!;
        public TimeProvider Clock = null!;
    }

    readonly record struct Deferred(IOutboxPublication Publication, EntityEntry Entry, string Id);

    // ── SavingChanges ───────────────────────────────────────────────────

    public InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (this.Capture(eventData.Context) is { NeedsTransaction: true } state)
            state.Owned = eventData.Context!.Database.BeginTransaction();
        return result;
    }

    public async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (this.Capture(eventData.Context) is { NeedsTransaction: true } state)
            state.Owned = await eventData.Context!.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    SaveState? Capture(DbContext? context)
    {
        if (context == null || (this.states.TryGetValue(context, out var current) && current.Flushing))
            return null;

        // Interceptors run before SaveChanges' own DetectChanges, so without this a property edited on a tracked
        // entity would not yet read as Modified here.
        if (context.ChangeTracker.AutoDetectChangesEnabled)
            context.ChangeTracker.DetectChanges();

        var candidates = context.ChangeTracker
            .Entries()
            .Where(e => e.Entity is not OutboxMessage)
            .Select(e => (Entry: e, Operation: OperationOf(e.State)))
            .Where(x => x.Operation != OutboxOperations.None)
            .SelectMany(x => publications.Where(p => p.Matches(x.Entry, x.Operation)).Select(p => (x.Entry, x.Operation, Publication: p)))
            .ToList();

        if (candidates.Count == 0)
            return null;

        OutboxGuard.EnsureSupported(context);
        var state = new SaveState
        {
            Options = OutboxGuard.ResolveOptions(context),
            Clock = OutboxGuard.ResolveClock(context)
        };
        var now = state.Clock.GetUtcNow();

        // Every message is built before any is tracked: a factory that throws must not leave half a set of
        // messages in the change tracker to be saved by the caller's retry.
        foreach (var (entry, operation, publication) in candidates)
        {
            // Ids are reserved now, in entry order, so deferred messages still sort FIFO with the others.
            var id = OutboxId.Next();
            if (operation == OutboxOperations.Insert && entry.Properties.Any(p => p.IsTemporary))
                state.Deferred.Add(new Deferred(publication, entry, id));
            else if (publication.Build(entry, operation, id, state.Options, now) is { } message)
                state.Added.Add(message);
        }

        if (state.Added.Count == 0 && state.Deferred.Count == 0)
            return null;

        context.Set<OutboxMessage>().AddRange(state.Added);

        var hasTransaction = context.Database.CurrentTransaction != null || System.Transactions.Transaction.Current != null;
        state.NeedsTransaction = !hasTransaction &&
            // Deferred messages are a second write; and a context that opted out of EF's automatic transaction
            // would otherwise write the entities and the messages as separate, individually committed statements.
            (state.Deferred.Count > 0 || context.Database.AutoTransactionBehavior == AutoTransactionBehavior.Never);

        this.states.AddOrUpdate(context, state);
        return state;
    }

    static OutboxOperations OperationOf(EntityState state) => state switch
    {
        EntityState.Added => OutboxOperations.Insert,
        EntityState.Modified => OutboxOperations.Update,
        EntityState.Deleted => OutboxOperations.Delete,
        _ => OutboxOperations.None
    };

    // ── SavedChanges ────────────────────────────────────────────────────

    public int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        var context = eventData.Context;
        if (context == null || !this.states.TryGetValue(context, out var state) || state.Flushing)
            return result;

        var flushed = 0;
        try
        {
            var deferred = this.BuildDeferred(context, state);
            if (deferred.Count > 0)
            {
                state.Flushing = true;
                flushed = context.SaveChanges(acceptAllChangesOnSuccess: true);
            }
            state.Owned?.Commit();
        }
        catch
        {
            Untrack(context, state);
            throw;
        }
        finally
        {
            state.Owned?.Dispose();
            this.states.Remove(context);
        }
        return result + flushed;
    }

    public async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        var context = eventData.Context;
        if (context == null || !this.states.TryGetValue(context, out var state) || state.Flushing)
            return result;

        var flushed = 0;
        try
        {
            var deferred = this.BuildDeferred(context, state);
            if (deferred.Count > 0)
            {
                state.Flushing = true;
                flushed = await context.SaveChangesAsync(acceptAllChangesOnSuccess: true, cancellationToken).ConfigureAwait(false);
            }
            if (state.Owned != null)
                await state.Owned.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Untrack(context, state);
            throw;
        }
        finally
        {
            if (state.Owned != null)
                await state.Owned.DisposeAsync().ConfigureAwait(false);
            this.states.Remove(context);
        }
        return result + flushed;
    }

    List<OutboxMessage> BuildDeferred(DbContext context, SaveState state)
    {
        if (state.Deferred.Count == 0)
            return [];

        // With acceptAllChangesOnSuccess: false the inserted entities are still Added, and the second pass would
        // insert them all over again. There is no way to save only the messages, so refuse rather than duplicate.
        if (state.Deferred.Any(d => d.Entry.State == EntityState.Added))
            throw new InvalidOperationException(
                "Outbox publishing cannot defer an event for a store-generated key when SaveChanges(acceptAllChangesOnSuccess: false) is used. " +
                "Call SaveChanges() with acceptAllChangesOnSuccess: true, or use client-generated keys for published entities.");

        var now = state.Clock.GetUtcNow();
        var messages = state.Deferred
            .Select(d => d.Publication.Build(d.Entry, OutboxOperations.Insert, d.Id, state.Options, now))
            .OfType<OutboxMessage>()
            .ToList();

        context.Set<OutboxMessage>().AddRange(messages);
        state.Added.AddRange(messages);
        return messages;
    }

    // ── Failure ─────────────────────────────────────────────────────────

    public void SaveChangesFailed(DbContextErrorEventData eventData) => this.Abandon(eventData.Context);

    public Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
        => this.AbandonAsync(eventData.Context);

    public void SaveChangesCanceled(DbContextEventData eventData) => this.Abandon(eventData.Context);

    public Task SaveChangesCanceledAsync(DbContextEventData eventData, CancellationToken cancellationToken = default)
        => this.AbandonAsync(eventData.Context);

    void Abandon(DbContext? context)
    {
        if (context != null && this.states.TryGetValue(context, out var state) && !state.Flushing)
        {
            Untrack(context, state);
            state.Owned?.Dispose();
            this.states.Remove(context);
        }
    }

    async Task AbandonAsync(DbContext? context)
    {
        if (context != null && this.states.TryGetValue(context, out var state) && !state.Flushing)
        {
            Untrack(context, state);
            if (state.Owned != null)
                await state.Owned.DisposeAsync().ConfigureAwait(false);
            this.states.Remove(context);
        }
    }

    // A failed save leaves the caller's entities tracked so they can retry; the messages this interceptor added
    // must not stay behind, or the retry would publish every event twice.
    static void Untrack(DbContext context, SaveState state)
    {
        foreach (var message in state.Added)
        {
            var entry = context.Entry(message);
            if (entry.State == EntityState.Added)
                entry.State = EntityState.Detached;
        }
    }
}
