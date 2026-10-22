using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Shiny.EntityFrameworkCore.Temporal.Internal;

/// <summary>
/// Records history for <c>HasHistory()</c> entities, atomically with the save that changed them.
/// </summary>
/// <remarks>
/// <para>
/// Changes are captured in <c>SavingChanges</c>, but written only once store-generated values (identity keys,
/// row versions, computed columns) have been propagated back to the entries, and always inside the transaction
/// that carries the entity writes:
/// </para>
/// <list type="bullet">
/// <item>No transaction in flight: the context's <c>AutoTransactionBehavior</c> is raised to <c>Always</c> for this
/// save, so EF opens its own transaction even for a single statement, and history is written from
/// <c>TransactionCommitting</c> - after the batch ran, before it commits. This stays inside EF's execution
/// strategy, so retrying strategies (<c>EnableRetryOnFailure</c>) keep working and a retry rewrites history with
/// the retried batch.</item>
/// <item>A user transaction (or an ambient <c>System.Transactions</c> one) is in flight: history is written from
/// <c>SavedChanges</c> into that transaction, so it commits or rolls back with the user's work.</item>
/// </list>
/// <para>The interceptor is stateless and shared; per-save state hangs off the context instance.</para>
/// </remarks>
sealed class HistoryInterceptor : ISaveChangesInterceptor, IDbTransactionInterceptor
{
    public static readonly HistoryInterceptor Instance = new();

    readonly ConditionalWeakTable<DbContext, SaveState> states = new();

    sealed class SaveState
    {
        public required List<PendingChange> Changes { get; init; }
        public required TemporalHistoryOptions Options { get; init; }
        public required bool AtCommit { get; init; }
        public AutoTransactionBehavior? PreviousBehavior { get; init; }
    }

    // ── SaveChanges ──────────────────────────────────────────────────────

    public InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        this.Capture(eventData.Context);
        return result;
    }

    public ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        this.Capture(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        this.CompleteAsync(eventData.Context, false, default).GetAwaiter().GetResult();
        return result;
    }

    public async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        await this.CompleteAsync(eventData.Context, true, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public void SaveChangesFailed(DbContextErrorEventData eventData) => this.Reset(eventData.Context);

    public Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        this.Reset(eventData.Context);
        return Task.CompletedTask;
    }

    public void SaveChangesCanceled(DbContextEventData eventData) => this.Reset(eventData.Context);

    public Task SaveChangesCanceledAsync(DbContextEventData eventData, CancellationToken cancellationToken = default)
    {
        this.Reset(eventData.Context);
        return Task.CompletedTask;
    }

    // ── Transactions ─────────────────────────────────────────────────────

    public InterceptionResult TransactionCommitting(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result)
    {
        this.WriteAtCommitAsync(eventData.Context, false, default).GetAwaiter().GetResult();
        return result;
    }

    public async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        await this.WriteAtCommitAsync(eventData.Context, true, cancellationToken).ConfigureAwait(false);
        return result;
    }

    // ── Core ─────────────────────────────────────────────────────────────

    void Capture(DbContext? context)
    {
        if (context == null)
            return;

        this.Reset(context);
        var options = TemporalHistoryOptionsExtension.Find(context);
        if (options == null)
            return;

        var changes = Collect(context);
        if (changes.Count == 0)
            return;

        var database = context.Database;
        var inTransaction = database.CurrentTransaction != null || System.Transactions.Transaction.Current != null;

        AutoTransactionBehavior? previous = null;
        if (!inTransaction)
        {
            previous = database.AutoTransactionBehavior;
            database.AutoTransactionBehavior = AutoTransactionBehavior.Always;
        }

        this.states.AddOrUpdate(context, new SaveState
        {
            Changes = changes,
            Options = options,
            AtCommit = !inTransaction,
            PreviousBehavior = previous
        });
    }

    async Task WriteAtCommitAsync(DbContext? context, bool async, CancellationToken ct)
    {
        if (context != null && this.states.TryGetValue(context, out var state) && state.AtCommit)
            await HistoryWriter.WriteAsync(context, state.Changes, state.Options, async, ct).ConfigureAwait(false);
    }

    async Task CompleteAsync(DbContext? context, bool async, CancellationToken ct)
    {
        if (context == null || !this.states.TryGetValue(context, out var state))
            return;

        try
        {
            if (!state.AtCommit)
                await HistoryWriter.WriteAsync(context, state.Changes, state.Options, async, ct).ConfigureAwait(false);
        }
        finally
        {
            this.Reset(context);
        }
    }

    void Reset(DbContext? context)
    {
        if (context != null && this.states.TryGetValue(context, out var state))
        {
            this.states.Remove(context);
            if (state.PreviousBehavior is { } previous)
                context.Database.AutoTransactionBehavior = previous;
        }
    }

    /// <summary>
    /// The history-relevant changes in the change tracker: adds, modifications and deletions of mapped entities,
    /// plus any change to an owned entity, which is attributed to its (otherwise unchanged) mapped owner as an update.
    /// </summary>
    static List<PendingChange> Collect(DbContext context)
    {
        if (context.ChangeTracker.AutoDetectChangesEnabled)
            context.ChangeTracker.DetectChanges();

        var changes = new List<PendingChange>();
        var changedEntities = new HashSet<object>(ReferenceEqualityComparer.Instance);
        List<EntityEntry>? ownedChanges = null;

        foreach (var entry in context.ChangeTracker.Entries())
        {
            var changed = entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted;
            if (changed && entry.Metadata.IsOwned())
            {
                (ownedChanges ??= []).Add(entry);
            }
            else if (changed && HistoryMapping.Find(entry.Metadata) is { } mapping)
            {
                var operation = entry.State switch
                {
                    EntityState.Added => TemporalOperation.Inserted,
                    EntityState.Deleted => TemporalOperation.Removed,
                    _ => TemporalOperation.Updated
                };
                changes.Add(new PendingChange
                {
                    Entry = entry,
                    Mapping = mapping,
                    Operation = operation,
                    Key = operation == TemporalOperation.Inserted ? null : KeyFormatter.FromEntry(entry, original: false)
                });
                changedEntities.Add(entry.Entity);
            }
        }

        if (ownedChanges != null)
            AttributeOwnedChanges(context, ownedChanges, changes, changedEntities);

        return changes;
    }

    static void AttributeOwnedChanges(DbContext context, List<EntityEntry> ownedChanges, List<PendingChange> changes, HashSet<object> changedEntities)
    {
        // Owners that aren't otherwise changed, by (history name, key). Built only when an owned value changed.
        Dictionary<(string, string), (EntityEntry Entry, HistoryMapping Mapping)>? owners = null;

        foreach (var owned in ownedChanges)
        {
            var rootType = HistoryMapping.OwnershipRoot(owned.Metadata);
            if (HistoryMapping.Find(rootType) is { } mapping)
            {
                owners ??= UnchangedOwners(context);

                // An owned entity's ownership FK starts with its root owner's key (owned keys are derived from the
                // owner's), however deep the ownership chain goes.
                var rootKey = rootType.FindPrimaryKey()!;
                var fk = owned.Metadata.FindOwnership()!.Properties;
                var values = new object?[rootKey.Properties.Count];
                for (var i = 0; i < values.Length; i++)
                    values[i] = owned.Property(fk[i].Name).CurrentValue;

                var key = KeyFormatter.Format(rootKey, values);
                if (owners.Remove((mapping.Name, key), out var owner) && !changedEntities.Contains(owner.Entry.Entity))
                {
                    changes.Add(new PendingChange { Entry = owner.Entry, Mapping = owner.Mapping, Operation = TemporalOperation.Updated, Key = key });
                    changedEntities.Add(owner.Entry.Entity);
                }
            }
        }
    }

    static Dictionary<(string, string), (EntityEntry, HistoryMapping)> UnchangedOwners(DbContext context)
    {
        var owners = new Dictionary<(string, string), (EntityEntry, HistoryMapping)>();
        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State == EntityState.Unchanged && !entry.Metadata.IsOwned() && HistoryMapping.Find(entry.Metadata) is { } mapping)
                owners[(mapping.Name, KeyFormatter.FromEntry(entry, original: false))] = (entry, mapping);
        }
        return owners;
    }
}
