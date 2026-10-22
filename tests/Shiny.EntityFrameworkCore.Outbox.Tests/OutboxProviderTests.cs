using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Shiny.EntityFrameworkCore.Outbox.Tests;

public sealed class SqliteOutboxTests(SqliteFixture fixture) : OutboxProviderTests(fixture), IClassFixture<SqliteFixture>;
public sealed class PostgresOutboxTests(PostgresFixture fixture) : OutboxProviderTests(fixture), IClassFixture<PostgresFixture>;
public sealed class SqlServerOutboxTests(SqlServerFixture fixture) : OutboxProviderTests(fixture), IClassFixture<SqlServerFixture>;
public sealed class MySqlOutboxTests(MySqlFixture fixture) : OutboxProviderTests(fixture), IClassFixture<MySqlFixture>;

/// <summary>Every behaviour that touches a database, run against each relational provider.</summary>
public abstract class OutboxProviderTests(ProviderFixture provider) : IAsyncLifetime
{
    string database = null!;

    public async ValueTask InitializeAsync()
        => this.database = await provider.CreateDatabase<TestDbContext>(o => new TestDbContext(o));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    DbContextOptions<TestDbContext> Options(Action<DbContextOptionsBuilder<TestDbContext>>? configure = null)
        => provider.Options(this.database, configure);

    TestDbContext Context(Action<DbContextOptionsBuilder<TestDbContext>>? configure = null) => new(this.Options(configure));

    TestDbContext Publishing(Action<OutboxPublishingBuilder> publish, bool failOutboxInsert = false)
        => this.Context(o =>
        {
            o.AddOutboxPublishing(publish);
            if (failOutboxInsert)
                o.AddInterceptors(new FailOutboxInsertInterceptor());
        });

    OutboxRunner Runner(IOutboxDispatcher dispatcher, ManualClock clock, Action<OutboxOptions>? configure = null)
    {
        var options = new OutboxOptions { Backoff = _ => TimeSpan.FromMinutes(1) };
        configure?.Invoke(options);
        return new OutboxRunner(() => this.Context(), dispatcher, options, clock);
    }

    async Task<List<OutboxMessage>> Messages()
    {
        await using var db = this.Context();
        return await db.Set<OutboxMessage>().AsNoTracking().OrderBy(m => m.Id).ToListAsync(Ct);
    }

    async Task<OutboxMessage> Message(string id)
    {
        await using var db = this.Context();
        return await db.Set<OutboxMessage>().AsNoTracking().SingleAsync(m => m.Id == id, Ct);
    }

    async Task<OutboxMessage> Seed(Action<OutboxMessage> shape)
    {
        await using var db = this.Context();
        var message = db.EnqueueRaw("seeded", "{}");
        shape(message);
        await db.SaveChangesAsync(Ct);
        return message;
    }

    static void AssertClose(DateTimeOffset expected, DateTimeOffset? actual)
    {
        Assert.NotNull(actual);
        // Providers store microseconds (PostgreSQL, MySQL) or ticks; a millisecond covers every one of them.
        Assert.InRange((actual!.Value - expected).Duration(), TimeSpan.Zero, TimeSpan.FromMilliseconds(1));
    }

    // ── Enqueue ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Enqueue_CommitsWithTheEntityInOneSaveChanges()
    {
        var customer = new Customer { Id = Guid.NewGuid(), Name = "Ada" };
        await using (var db = this.Context())
        {
            db.Customers.Add(customer);
            var tracked = db.Enqueue(new CustomerCreated(customer.Id, customer.Name), partitionKey: "c1");
            Assert.Equal(EntityState.Added, db.Entry(tracked).State);
            await db.SaveChangesAsync(Ct);
        }

        var message = Assert.Single(await this.Messages());
        Assert.Equal(typeof(CustomerCreated).FullName, message.MessageType);
        Assert.Equal("c1", message.PartitionKey);
        Assert.Contains("\"customerId\"", message.Payload);           // camelCase by default
        Assert.Equal(0, message.Attempts);
        Assert.Equal(0, message.Version);
        Assert.Null(message.ProcessedAt);
        Assert.Null(message.Headers);                                // no ambient activity, no supplied headers
        Assert.Matches("^[0-9a-f]{32}$", message.Id);
    }

    [Fact]
    public async Task Enqueue_FailedSave_LeavesNoMessage()
    {
        var id = Guid.NewGuid();
        await using (var db = this.Context())
        {
            db.Customers.Add(new Customer { Id = id, Name = "first" });
            await db.SaveChangesAsync(Ct);
        }

        await using (var db = this.Context())
        {
            db.Customers.Add(new Customer { Id = id, Name = "duplicate key" });
            db.Enqueue(new CustomerCreated(id, "duplicate key"));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        }

        Assert.Empty(await this.Messages());
    }

    [Fact]
    public async Task Enqueue_RolledBackTransaction_LeavesNeitherRow()
    {
        await using (var db = this.Context())
        {
            await using var tx = await db.Database.BeginTransactionAsync(Ct);
            var customer = new Customer { Id = Guid.NewGuid(), Name = "rolled back" };
            db.Customers.Add(customer);
            db.Enqueue(new CustomerCreated(customer.Id, customer.Name));
            await db.SaveChangesAsync(Ct);
            await tx.RollbackAsync(Ct);
        }

        await using var check = this.Context();
        Assert.Equal(0, await check.Customers.CountAsync(Ct));
        Assert.Empty(await this.Messages());
    }

    [Fact]
    public async Task EnqueueRaw_StoresPayloadAndHeadersVerbatim()
    {
        await using (var db = this.Context())
        {
            db.EnqueueRaw("contract.OrderPlaced.v2", "{\"Raw\":true}", headers: new Dictionary<string, string> { ["tenant"] = "t1" });
            await db.SaveChangesAsync(Ct);
        }

        var message = Assert.Single(await this.Messages());
        Assert.Equal("contract.OrderPlaced.v2", message.MessageType);
        Assert.Equal("{\"Raw\":true}", message.Payload);
        Assert.Equal("t1", message.Headers!["tenant"]);
    }

    [Fact]
    public async Task Enqueue_UsesTheRegisteredPayloadOptionsAndClock()
    {
        var clock = new ManualClock();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddDbContext<TestDbContext>(o => provider.Use(o, this.database));
        services.AddDbContextOutbox<TestDbContext>(
            (_, _) => Task.CompletedTask,
            o => o.PayloadSerializerOptions = new JsonSerializerOptions());   // PascalCase

        await using var sp = services.BuildServiceProvider();
        await using (var scope = sp.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TestDbContext>();
            db.Enqueue(new CustomerCreated(Guid.NewGuid(), "Pascal"));
            await db.SaveChangesAsync(Ct);
        }

        var message = Assert.Single(await this.Messages());
        Assert.Contains("\"CustomerId\"", message.Payload);
        AssertClose(clock.GetUtcNow(), message.CreatedAt);
    }

    // ── Declarative publishing ──────────────────────────────────────────

    static void PublishOrders(OutboxPublishingBuilder p, OutboxOperations operations = OutboxOperations.All)
        => p.Publish<Order>(
            ctx => new OrderChanged(ctx.Entity.Id, ctx.Entity.Status, ctx.Operation),
            operations,
            partitionKey: ctx => "order-" + ctx.Entity.Id);

    [Fact]
    public async Task Publish_IdentityKey_EventCarriesTheGeneratedKey()
    {
        var first = new Order { Status = "New", Total = 10 };
        var second = new Order { Status = "New", Total = 20 };
        await using (var db = this.Publishing(p => PublishOrders(p)))
        {
            db.Orders.AddRange(first, second);
            var saved = await db.SaveChangesAsync(Ct);
            Assert.Equal(4, saved);                                  // two orders + two messages
            Assert.Null(db.Database.CurrentTransaction);             // the interceptor's own transaction is gone
        }

        Assert.True(first.Id > 0 && second.Id > 0);
        var messages = await this.Messages();
        Assert.Equal(2, messages.Count);

        // Ids were reserved in entry order before the save, so FIFO still follows the entries.
        var events = messages.Select(m => JsonSerializer.Deserialize<OrderChanged>(m.Payload, OutboxGuard.DefaultOptions.PayloadSerializerOptions)!).ToList();
        Assert.Equal([first.Id, second.Id], events.Select(e => e.OrderId));
        Assert.All(events, e => Assert.Equal(OutboxOperations.Insert, e.Operation));
        Assert.Equal(["order-" + first.Id, "order-" + second.Id], messages.Select(m => m.PartitionKey));
    }

    [Fact]
    public async Task Publish_IdentityKey_SyncSaveChangesWorksToo()
    {
        var order = new Order { Total = 5 };
        using (var db = this.Publishing(p => PublishOrders(p)))
        {
            db.Orders.Add(order);
            Assert.Equal(2, db.SaveChanges());
        }

        var message = Assert.Single(await this.Messages());
        Assert.Contains($"\"orderId\":{order.Id}", message.Payload);
    }

    [Fact]
    public async Task Publish_IdentityKey_FailingMessageInsert_RollsBackTheEntity()
    {
        await using (var db = this.Publishing(p => PublishOrders(p), failOutboxInsert: true))
        {
            db.Orders.Add(new Order { Total = 1 });
            var error = await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync(Ct));
            Assert.Contains("outbox insert refused", error.ToString());
            Assert.Null(db.Database.CurrentTransaction);
        }

        await using var check = this.Context();
        Assert.Equal(0, await check.Orders.CountAsync(Ct));
        Assert.Empty(await this.Messages());
    }

    [Fact]
    public async Task Publish_IdentityKey_SyncFailingMessageInsert_RollsBackTheEntity()
    {
        using (var db = this.Publishing(p => PublishOrders(p), failOutboxInsert: true))
        {
            db.Orders.Add(new Order { Total = 1 });
            Assert.ThrowsAny<Exception>(() => db.SaveChanges());
        }

        await using var check = this.Context();
        Assert.Equal(0, await check.Orders.CountAsync(Ct));
        Assert.Empty(await this.Messages());
    }

    [Fact]
    public async Task Publish_ClientKey_FailingEntitySave_LeavesNoMessage_AndARetryPublishesOnce()
    {
        var id = Guid.NewGuid();
        await using (var db = this.Context())
        {
            db.Customers.Add(new Customer { Id = id, Name = "taken" });
            await db.SaveChangesAsync(Ct);
        }

        await using (var db = this.Publishing(p => p.Publish<Customer>(ctx => new CustomerCreated(ctx.Entity.Id, ctx.Entity.Name))))
        {
            var customer = new Customer { Id = id, Name = "clash" };
            db.Customers.Add(customer);
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
            Assert.Empty(await this.Messages());

            // The failed save left the customer tracked for a retry; the message must not have been left with it.
            Assert.DoesNotContain(db.ChangeTracker.Entries<OutboxMessage>(), _ => true);
            db.Entry(customer).State = EntityState.Detached;
            customer.Id = Guid.NewGuid();
            db.Customers.Add(customer);
            await db.SaveChangesAsync(Ct);
        }

        var message = Assert.Single(await this.Messages());
        Assert.Contains("clash", message.Payload);
    }

    [Fact]
    public async Task Publish_ClientKey_FailingMessageInsert_RollsBackTheEntity()
    {
        await using (var db = this.Publishing(p => p.Publish<Customer>(ctx => new CustomerCreated(ctx.Entity.Id, ctx.Entity.Name)), failOutboxInsert: true))
        {
            db.Customers.Add(new Customer { Id = Guid.NewGuid(), Name = "x" });
            await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync(Ct));
        }

        await using var check = this.Context();
        Assert.Equal(0, await check.Customers.CountAsync(Ct));
    }

    [Fact]
    public async Task Publish_AutoTransactionNever_StillWritesAtomically()
    {
        await using (var db = this.Publishing(p => p.Publish<Customer>(ctx => new CustomerCreated(ctx.Entity.Id, ctx.Entity.Name)), failOutboxInsert: true))
        {
            db.Database.AutoTransactionBehavior = AutoTransactionBehavior.Never;
            db.Customers.Add(new Customer { Id = Guid.NewGuid(), Name = "x" });
            await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync(Ct));
        }

        await using (var check = this.Context())
            Assert.Equal(0, await check.Customers.CountAsync(Ct));

        // The opt-out is overridden for a save that publishes: the interceptor opens the transaction EF would not.
        var transactions = new TransactionCounter();
        await using (var db = this.Context(o => o
            .AddOutboxPublishing(p => p.Publish<Customer>(ctx => new CustomerCreated(ctx.Entity.Id, ctx.Entity.Name)))
            .AddInterceptors(transactions)))
        {
            db.Database.AutoTransactionBehavior = AutoTransactionBehavior.Never;
            db.Customers.Add(new Customer { Id = Guid.NewGuid(), Name = "y" });
            await db.SaveChangesAsync(Ct);
            Assert.Null(db.Database.CurrentTransaction);
        }
        Assert.Equal(1, transactions.Started);
        Assert.Single(await this.Messages());
    }

    [Fact]
    public async Task Publish_OperationFilter_OnlyPublishesTheChosenOperations()
    {
        await using (var db = this.Publishing(p => PublishOrders(p, OutboxOperations.Insert | OutboxOperations.Delete)))
        {
            var order = new Order { Total = 1 };
            db.Orders.Add(order);
            await db.SaveChangesAsync(Ct);

            order.Status = "Shipped";
            await db.SaveChangesAsync(Ct);

            db.Orders.Remove(order);
            await db.SaveChangesAsync(Ct);
        }

        var operations = (await this.Messages())
            .Select(m => JsonSerializer.Deserialize<OrderChanged>(m.Payload, OutboxGuard.DefaultOptions.PayloadSerializerOptions)!.Operation)
            .ToList();
        Assert.Equal([OutboxOperations.Insert, OutboxOperations.Delete], operations);
    }

    [Fact]
    public async Task Publish_Update_SeesOriginalValues_AndNullPublishesNothing()
    {
        var order = new Order { Status = "New", Total = 1 };
        await using (var db = this.Context())
        {
            db.Orders.Add(order);
            await db.SaveChangesAsync(Ct);
        }

        await using (var db = this.Publishing(p => p.Publish<Order>(
            ctx => ctx.Entry.Property(x => x.Status).IsModified
                ? new StatusChanged(ctx.Entity.Id, ctx.Entry.Property(x => x.Status).OriginalValue, ctx.Entity.Status)
                : null,
            OutboxOperations.Update)))
        {
            var tracked = await db.Orders.SingleAsync(Ct);
            tracked.Total = 99;                                      // not a status change: nothing published
            await db.SaveChangesAsync(Ct);
            Assert.Empty(await this.Messages());

            tracked.Status = "Paid";
            await db.SaveChangesAsync(Ct);
        }

        var message = Assert.Single(await this.Messages());
        Assert.Equal(typeof(StatusChanged).FullName, message.MessageType);
        var evt = JsonSerializer.Deserialize<StatusChanged>(message.Payload, OutboxGuard.DefaultOptions.PayloadSerializerOptions)!;
        Assert.Equal(("New", "Paid"), (evt.From, evt.To));
    }

    [Fact]
    public async Task Publish_JoinsTheCallersTransaction()
    {
        await using (var db = this.Publishing(p => PublishOrders(p)))
        {
            await using var tx = await db.Database.BeginTransactionAsync(Ct);
            db.Orders.Add(new Order { Total = 1 });
            await db.SaveChangesAsync(Ct);
            Assert.Same(tx, db.Database.CurrentTransaction);         // joined, not replaced or committed
            await tx.RollbackAsync(Ct);
        }

        await using (var check = this.Context())
            Assert.Equal(0, await check.Orders.CountAsync(Ct));
        Assert.Empty(await this.Messages());

        await using (var db = this.Publishing(p => PublishOrders(p)))
        {
            await using var tx = await db.Database.BeginTransactionAsync(Ct);
            db.Orders.Add(new Order { Total = 2 });
            await db.SaveChangesAsync(Ct);
            await tx.CommitAsync(Ct);
        }
        Assert.Single(await this.Messages());
    }

    [Fact]
    public async Task Publish_MatchesAssignableTypes_ButNeverTheOutboxItself()
    {
        await using (var db = this.Publishing(p => p.Publish<object>(ctx => new { Kind = ctx.Entity.GetType().Name })))
        {
            db.Customers.Add(new Customer { Id = Guid.NewGuid(), Name = "a" });
            db.Orders.Add(new Order { Total = 1 });
            db.EnqueueRaw("explicit", "{}");
            await db.SaveChangesAsync(Ct);
        }

        var payloads = (await this.Messages()).Select(m => m.Payload).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["{\"kind\":\"Customer\"}", "{\"kind\":\"Order\"}", "{}"], payloads);
    }

    [Fact]
    public async Task Publish_AndExplicitEnqueue_ShareOneSave()
    {
        await using (var db = this.Publishing(p => PublishOrders(p)))
        {
            db.Orders.Add(new Order { Total = 1 });
            db.EnqueueRaw("explicit", "{}");
            await db.SaveChangesAsync(Ct);
        }

        var types = (await this.Messages()).Select(m => m.MessageType).Order(StringComparer.Ordinal).ToList();
        Assert.Equal([typeof(OrderChanged).FullName!, "explicit"], types);
    }

    [Fact]
    public async Task Publish_IdentityKey_WithoutAcceptAllChanges_RefusesRatherThanDuplicating()
    {
        await using (var db = this.Publishing(p => PublishOrders(p)))
        {
            db.Orders.Add(new Order { Total = 1 });
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(acceptAllChangesOnSuccess: false, Ct));
        }

        await using var check = this.Context();
        Assert.Equal(0, await check.Orders.CountAsync(Ct));
        Assert.Empty(await this.Messages());
    }

    // ── Processing ──────────────────────────────────────────────────────

    async Task<List<OutboxMessage>> EnqueueMany(int count, Func<int, string?>? partitionKey = null)
    {
        await using var db = this.Context();
        var messages = Enumerable.Range(0, count).Select(i => db.EnqueueRaw("seq", $"{{\"n\":{i}}}", partitionKey?.Invoke(i))).ToList();
        await db.SaveChangesAsync(Ct);
        return messages;
    }

    [Fact]
    public async Task DrainOnce_DispatchesFifoAndAcknowledges()
    {
        var enqueued = await this.EnqueueMany(10);
        var dispatcher = new RecordingDispatcher();
        var runner = this.Runner(dispatcher, new ManualClock(), o => o.MaxParallelism = 1);

        Assert.Equal(10, await runner.PendingDepth(Ct));
        Assert.Equal(10, await runner.DrainOnce(Ct));

        Assert.Equal(enqueued.Select(m => m.Id), dispatcher.Dispatched.Select(m => m.Id));
        var stored = await this.Messages();
        Assert.All(stored, m =>
        {
            Assert.NotNull(m.ProcessedAt);
            Assert.Equal(1, m.Attempts);
            Assert.Equal(2, m.Version);                              // the claim and the acknowledgement
            Assert.Null(m.Error);
        });
        Assert.Equal(0, await runner.DrainOnce(Ct));
        Assert.Equal(0, await runner.PendingDepth(Ct));
    }

    [Fact]
    public async Task DrainOnce_RespectsBatchSize()
    {
        await this.EnqueueMany(5);
        var dispatcher = new RecordingDispatcher();
        var runner = this.Runner(dispatcher, new ManualClock(), o => o.BatchSize = 2);

        Assert.Equal(2, await runner.DrainOnce(Ct));
        Assert.Equal(2, await runner.DrainOnce(Ct));
        Assert.Equal(1, await runner.DrainOnce(Ct));
    }

    [Fact]
    public async Task CompetingRunners_NeverDispatchAMessageTwice()
    {
        const int count = 60;
        await this.EnqueueMany(count);
        var dispatched = new ConcurrentBag<string>();
        var dispatcher = new RecordingDispatcher(async m =>
        {
            dispatched.Add(m.Id);
            await Task.Yield();
        });
        var clock = new ManualClock();
        var a = this.Runner(dispatcher, clock, o => { o.BatchSize = 100; o.MaxParallelism = 6; });
        var b = this.Runner(dispatcher, clock, o => { o.BatchSize = 100; o.MaxParallelism = 6; });

        var results = await Task.WhenAll(a.DrainOnce(Ct), b.DrainOnce(Ct));

        Assert.Equal(count, results.Sum());
        Assert.Equal(count, dispatched.Count);
        Assert.Equal(count, dispatched.Distinct().Count());
        Assert.All(await this.Messages(), m => Assert.Equal(1, m.Attempts));
    }

    [Fact]
    public async Task FailedDispatch_RecordsTheErrorAndBacksOff()
    {
        var message = Assert.Single(await this.EnqueueMany(1));
        var clock = new ManualClock();
        var fail = true;
        var dispatcher = new RecordingDispatcher(_ => fail ? throw new InvalidOperationException("boom") : Task.CompletedTask);
        var runner = this.Runner(dispatcher, clock, o => o.Backoff = n => TimeSpan.FromMinutes(n * 10));

        Assert.Equal(1, await runner.DrainOnce(Ct));
        var failed = await this.Message(message.Id);
        Assert.Equal(1, failed.Attempts);
        Assert.Equal("InvalidOperationException: boom", failed.Error);
        Assert.Null(failed.ProcessedAt);
        Assert.Null(failed.DeadLetteredAt);
        AssertClose(clock.GetUtcNow() + TimeSpan.FromMinutes(10), failed.AvailableAt);

        // Still inside the backoff: invisible.
        Assert.Equal(0, await runner.DrainOnce(Ct));
        clock.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal(0, await runner.DrainOnce(Ct));

        clock.Advance(TimeSpan.FromMinutes(2));
        fail = false;
        Assert.Equal(1, await runner.DrainOnce(Ct));
        var delivered = await this.Message(message.Id);
        Assert.Equal(2, delivered.Attempts);
        Assert.NotNull(delivered.ProcessedAt);
        Assert.Null(delivered.Error);
    }

    [Fact]
    public async Task AbandonedClaim_ReturnsToTheQueueAfterTheVisibilityTimeout()
    {
        var message = Assert.Single(await this.EnqueueMany(1));
        var clock = new ManualClock();
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var dispatcher = new RecordingDispatcher(async _ =>
        {
            // The host stops mid-dispatch: the claim is already written, the outcome never is.
            await shutdown.CancelAsync();
            shutdown.Token.ThrowIfCancellationRequested();
        });
        var runner = this.Runner(dispatcher, clock);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.DrainOnce(shutdown.Token));

        var claimed = await this.Message(message.Id);
        Assert.Equal(1, claimed.Attempts);
        Assert.Null(claimed.Error);                                  // shutdown is not a delivery failure
        Assert.Null(claimed.ProcessedAt);

        var next = new RecordingDispatcher();
        var recovery = this.Runner(next, clock);
        Assert.Equal(0, await recovery.DrainOnce(Ct));
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(1, await recovery.DrainOnce(Ct));
        Assert.Equal(message.Id, Assert.Single(next.Dispatched).Id);
    }

    [Fact]
    public async Task MaxAttempts_DeadLetters_AndRetentionNeverPurgesADeadLetter()
    {
        var message = Assert.Single(await this.EnqueueMany(1));
        var clock = new ManualClock();
        var runner = this.Runner(
            new RecordingDispatcher(_ => throw new InvalidOperationException("poison")),
            clock,
            o =>
            {
                o.MaxAttempts = 2;
                o.Backoff = _ => TimeSpan.Zero;
                o.Retention = TimeSpan.FromSeconds(1);
            });

        Assert.Equal(1, await runner.DrainOnce(Ct));
        Assert.Null((await this.Message(message.Id)).DeadLetteredAt);
        Assert.Equal(1, await runner.DrainOnce(Ct));

        var dead = await this.Message(message.Id);
        Assert.Equal(2, dead.Attempts);
        AssertClose(clock.GetUtcNow(), dead.DeadLetteredAt);
        Assert.Equal("InvalidOperationException: poison", dead.Error);
        Assert.Equal(0, await runner.DrainOnce(Ct));

        clock.Advance(TimeSpan.FromDays(30));
        Assert.Equal(0, await runner.PurgeExpired(Ct));
        Assert.Single(await this.Messages());
    }

    [Fact]
    public async Task ConcurrentOperatorWrite_WinsOverTheInFlightOutcome()
    {
        var message = Assert.Single(await this.EnqueueMany(1));
        var runner = this.Runner(new RecordingDispatcher(async m =>
        {
            // An operator edits the row while it is being dispatched.
            await using var db = this.Context();
            await db.Set<OutboxMessage>().Where(x => x.Id == m.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Version, x => x.Version + 1).SetProperty(x => x.Error, "operator"), Ct);
        }), new ManualClock());

        Assert.Equal(1, await runner.DrainOnce(Ct));

        var stored = await this.Message(message.Id);
        Assert.Null(stored.ProcessedAt);
        Assert.Equal("operator", stored.Error);
    }

    [Fact]
    public async Task OrderedPartitions_DeliverInOrder_AndAPoisonMessageBlocksOnlyItsPartition()
    {
        await using (var db = this.Context())
        {
            db.EnqueueRaw("poison", "{}", "a");
            db.EnqueueRaw("a2", "{}", "a");
            db.EnqueueRaw("a3", "{}", "a");
            db.EnqueueRaw("b1", "{}", "b");
            db.EnqueueRaw("b2", "{}", "b");
            db.EnqueueRaw("u1", "{}");
            await db.SaveChangesAsync(Ct);
        }

        var log = new ConcurrentQueue<string>();
        var dispatcher = new RecordingDispatcher(m =>
        {
            log.Enqueue(m.MessageType);
            return m.MessageType == "poison" ? throw new InvalidOperationException("poison") : Task.CompletedTask;
        });
        var runner = this.Runner(dispatcher, new ManualClock(), o =>
        {
            o.OrderedPartitions = true;
            o.MaxAttempts = 2;
            o.MaxParallelism = 4;
            o.Backoff = _ => TimeSpan.Zero;
        });

        // Pass 1: the poison message fails and holds back a2/a3; b is delivered in order; u1 is unordered.
        await runner.DrainOnce(Ct);
        var pass1 = log.ToList();
        Assert.DoesNotContain("a2", pass1);
        Assert.DoesNotContain("a3", pass1);
        Assert.True(pass1.IndexOf("b1") < pass1.IndexOf("b2"));
        Assert.Contains("u1", pass1);

        // Pass 2: it fails again and dead-letters — still blocking for this pass.
        log.Clear();
        await runner.DrainOnce(Ct);
        Assert.Equal(["poison"], log.ToList());

        // Pass 3: the partition moves on, in order.
        log.Clear();
        await runner.DrainOnce(Ct);
        Assert.Equal(["a2", "a3"], log.ToList());
    }

    [Fact]
    public async Task PurgeExpired_DeletesOnlyProcessedMessagesPastRetention()
    {
        var clock = new ManualClock();
        var now = clock.GetUtcNow();
        var old = await this.Seed(m => m.ProcessedAt = now.AddHours(-2));
        var recent = await this.Seed(m => m.ProcessedAt = now.AddMinutes(-10));
        var pending = await this.Seed(m => m.CreatedAt = now.AddDays(-10));

        var runner = this.Runner(new RecordingDispatcher(), clock, o => o.Retention = TimeSpan.FromHours(1));
        Assert.Equal(1, await runner.PurgeExpired(Ct));
        Assert.Equal([recent.Id, pending.Id], (await this.Messages()).Select(m => m.Id));
        Assert.DoesNotContain(old.Id, (await this.Messages()).Select(m => m.Id));

        var keepForever = this.Runner(new RecordingDispatcher(), clock, o => o.Retention = null);
        clock.Advance(TimeSpan.FromDays(365));
        Assert.Equal(0, await keepForever.PurgeExpired(Ct));
    }

    // ── Admin ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Admin_ReportsTheFourStates()
    {
        var clock = new ManualClock();
        var now = clock.GetUtcNow();
        var oldest = await this.Seed(m => m.CreatedAt = now.AddMinutes(-30));
        await this.Seed(m => m.CreatedAt = now.AddMinutes(-5));
        await this.Seed(m => m.AvailableAt = now.AddMinutes(5));                                // scheduled
        var deadOld = await this.Seed(m => m.DeadLetteredAt = now.AddMinutes(-20));
        var deadNew = await this.Seed(m => m.DeadLetteredAt = now.AddMinutes(-1));
        await this.Seed(m => m.ProcessedAt = now.AddMinutes(-1));                                // processed

        var admin = new OutboxAdmin(() => this.Context(), clock);
        Assert.Equal(2, await admin.PendingCount(Ct));
        Assert.Equal(1, await admin.ScheduledCount(Ct));
        Assert.Equal(2, await admin.DeadLetterCount(Ct));
        AssertClose(oldest.CreatedAt, await admin.OldestPendingAt(Ct));
        Assert.Equal([deadNew.Id, deadOld.Id], (await admin.DeadLetters(cancellationToken: Ct)).Select(m => m.Id));
        Assert.Equal([deadNew.Id], (await admin.DeadLetters(1, Ct)).Select(m => m.Id));
    }

    [Fact]
    public async Task Admin_OldestPendingAt_IsNullWhenNothingIsPending()
    {
        await this.Seed(m => m.ProcessedAt = DateTimeOffset.UtcNow);
        var admin = new OutboxAdmin(() => this.Context(), new ManualClock());
        Assert.Null(await admin.OldestPendingAt(Ct));
    }

    [Fact]
    public async Task Requeue_OnlyTouchesDeadLetters()
    {
        var clock = new ManualClock();
        var now = clock.GetUtcNow();
        var scheduled = await this.Seed(m => { m.AvailableAt = now.AddMinutes(5); m.Attempts = 3; });
        var processed = await this.Seed(m => m.ProcessedAt = now);
        var pending = await this.Seed(_ => { });
        var dead = await this.Seed(m => { m.DeadLetteredAt = now; m.Attempts = 8; m.Error = "x"; m.Version = 9; });

        var admin = new OutboxAdmin(() => this.Context(), clock);
        Assert.Equal(0, await admin.Requeue([scheduled.Id, processed.Id, pending.Id], Ct));
        Assert.Equal(0, await admin.Requeue([], Ct));
        Assert.Equal(3, (await this.Message(scheduled.Id)).Attempts);   // backoff not reset

        Assert.Equal(1, await admin.Requeue([dead.Id, dead.Id, scheduled.Id], Ct));
        var requeued = await this.Message(dead.Id);
        Assert.Null(requeued.DeadLetteredAt);
        Assert.Null(requeued.Error);
        Assert.Equal(0, requeued.Attempts);
        Assert.Equal(10, requeued.Version);
        AssertClose(now, requeued.AvailableAt);
        Assert.Equal(2, await admin.PendingCount(Ct));
    }

    [Fact]
    public async Task RequeueAllDeadLettered_CanNarrowToOneMessageType()
    {
        var now = DateTimeOffset.UtcNow;
        await this.Seed(m => { m.MessageType = "A"; m.DeadLetteredAt = now; });
        await this.Seed(m => { m.MessageType = "B"; m.DeadLetteredAt = now; });
        await this.Seed(m => { m.MessageType = "B"; m.DeadLetteredAt = now; });

        var admin = new OutboxAdmin(() => this.Context(), new ManualClock());
        Assert.Equal(1, await admin.RequeueAllDeadLettered("A", Ct));
        Assert.Equal(2, await admin.DeadLetterCount(Ct));
        Assert.Equal(2, await admin.RequeueAllDeadLettered(cancellationToken: Ct));
        Assert.Equal(0, await admin.DeadLetterCount(Ct));
    }

    [Fact]
    public async Task PurgeProcessed_NeverTouchesPendingOrDeadLetters()
    {
        var now = DateTimeOffset.UtcNow;
        var old = await this.Seed(m => m.ProcessedAt = now.AddDays(-2));
        var fresh = await this.Seed(m => m.ProcessedAt = now);
        var pending = await this.Seed(m => m.CreatedAt = now.AddDays(-9));
        var dead = await this.Seed(m => m.DeadLetteredAt = now.AddDays(-9));

        var admin = new OutboxAdmin(() => this.Context(), new ManualClock());
        Assert.Equal(1, await admin.PurgeProcessed(now.AddDays(-1), Ct));
        var remaining = (await this.Messages()).Select(m => m.Id).ToList();
        Assert.DoesNotContain(old.Id, remaining);
        Assert.Equal([fresh.Id, pending.Id, dead.Id], remaining);
    }

    // ── DI, scopes and the hosted processor ─────────────────────────────

    sealed class ScopeMarker
    {
        public Guid Id { get; } = Guid.NewGuid();
    }

    sealed class Sink
    {
        public ConcurrentBag<(Guid Scope, string MessageId)> Calls { get; } = [];
    }

    sealed class ScopedDispatcher(ScopeMarker marker, Sink sink, TestDbContext db) : IOutboxDispatcher
    {
        public async Task Dispatch(OutboxMessage message, CancellationToken cancellationToken)
        {
            // A scoped DbContext is usable from the dispatcher — proof the scope is a real one.
            await db.Customers.CountAsync(cancellationToken);
            sink.Calls.Add((marker.Id, message.Id));
        }
    }

    [Fact]
    public async Task Di_ResolvesTheDispatcherFromAFreshScopePerMessage()
    {
        await this.EnqueueMany(3);
        var services = new ServiceCollection();
        services.AddDbContext<TestDbContext>(o => provider.Use(o, this.database));
        services.AddScoped<ScopeMarker>();
        services.AddSingleton<Sink>();
        services.AddDbContextOutbox<TestDbContext, ScopedDispatcher>(o => o.MaxParallelism = 1);

        await using var sp = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var runner = sp.GetRequiredKeyedService<OutboxRunner>(typeof(TestDbContext));
        Assert.Same(runner, sp.GetRequiredService<OutboxRunner>());
        Assert.Same(sp.GetRequiredKeyedService<IOutboxAdmin>(typeof(TestDbContext)), sp.GetRequiredService<IOutboxAdmin>());

        Assert.Equal(3, await runner.DrainOnce(Ct));
        var calls = sp.GetRequiredService<Sink>().Calls;
        Assert.Equal(3, calls.Count);
        Assert.Equal(3, calls.Select(c => c.Scope).Distinct().Count());
        Assert.Equal(0, await sp.GetRequiredService<IOutboxAdmin>().PendingCount(Ct));
    }

    sealed class CountingFactory(Func<TestDbContext> create) : IDbContextFactory<TestDbContext>
    {
        public int Created;

        public TestDbContext CreateDbContext()
        {
            Interlocked.Increment(ref this.Created);
            return create();
        }
    }

    [Fact]
    public async Task Di_PrefersARegisteredDbContextFactory()
    {
        await this.EnqueueMany(2);
        var factory = new CountingFactory(() => this.Context());
        var received = new ConcurrentBag<string>();
        var services = new ServiceCollection();
        services.AddSingleton<IDbContextFactory<TestDbContext>>(factory);     // and no AddDbContext at all
        services.AddDbContextOutbox<TestDbContext>((m, _) =>
        {
            received.Add(m.Id);
            return Task.CompletedTask;
        });

        await using var sp = services.BuildServiceProvider();
        Assert.Equal(2, await sp.GetRequiredService<OutboxRunner>().DrainOnce(Ct));
        Assert.Equal(2, received.Count);
        Assert.True(factory.Created > 0);
    }

    [Fact]
    public async Task HostedProcessor_DrainsInTheBackground()
    {
        var delivered = new TaskCompletionSource<OutboxMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddDbContext<TestDbContext>(o => provider.Use(o, this.database));
        builder.Services.AddDbContextOutbox<TestDbContext>(
            (m, _) =>
            {
                delivered.TrySetResult(m);
                return Task.CompletedTask;
            },
            o => o.PollInterval = TimeSpan.FromMilliseconds(50));

        using var host = builder.Build();
        await host.StartAsync(Ct);
        try
        {
            await using (var scope = host.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TestDbContext>();
                db.EnqueueRaw("hosted", "{}");
                await db.SaveChangesAsync(Ct);
            }

            var message = await delivered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            Assert.Equal("hosted", message.MessageType);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    // ── Telemetry ───────────────────────────────────────────────────────

    [Fact]
    public async Task Dispatch_SpanJoinsAndLinksTheEnqueueingTrace()
    {
        var captured = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            // Only our own source — never the process-wide firehose, which other tests in the run also feed.
            ShouldListenTo = s => s.Name == OutboxDiagnostics.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = captured.Enqueue
        };
        ActivitySource.AddActivityListener(listener);

        ActivityTraceId traceId;
        ActivitySpanId spanId;
        string messageId;
        // A plain Activity needs no listener of its own, and its trace id is unique to this test.
        using (var enqueueing = new Activity("place-order").SetIdFormat(ActivityIdFormat.W3C).Start())
        {
            traceId = enqueueing.TraceId;
            spanId = enqueueing.SpanId;
            await using var db = this.Context();
            var message = db.EnqueueRaw("traced", "{}");
            messageId = message.Id;
            Assert.Equal(enqueueing.Id, message.Headers![OutboxColumns.TraceParentHeader]);
            await db.SaveChangesAsync(Ct);
        }

        await this.Runner(new RecordingDispatcher(), new ManualClock()).DrainOnce(Ct);

        var dispatch = Assert.Single(captured, a => a.TraceId == traceId);
        Assert.Equal(OutboxDiagnostics.DispatchActivityName, dispatch.OperationName);
        Assert.Equal(spanId, dispatch.ParentSpanId);
        Assert.Contains(dispatch.Links, l => l.Context.SpanId == spanId);
        Assert.Equal(messageId, dispatch.GetTagItem("messaging.message.id"));
        Assert.Equal("traced", dispatch.GetTagItem("messaging.message.type"));
    }

    [Fact]
    public async Task Dispatch_RecordsMetrics()
    {
        var type = "metrics-" + Guid.NewGuid().ToString("N");
        long dispatched = 0, deadLettered = 0;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == OutboxDiagnostics.Name)
                    l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            var mine = false;
            foreach (var tag in tags)
                mine |= tag.Key == "messaging.message.type" && (string?)tag.Value == type;
            if (mine && instrument.Name == "outbox.dispatched")
                Interlocked.Add(ref dispatched, value);
            if (mine && instrument.Name == "outbox.dead_lettered")
                Interlocked.Add(ref deadLettered, value);
        });
        listener.Start();

        await using (var db = this.Context())
        {
            db.EnqueueRaw(type, "{\"ok\":true}");
            db.EnqueueRaw(type, "{\"ok\":true}");
            db.EnqueueRaw(type, "{\"ok\":false}");
            await db.SaveChangesAsync(Ct);
        }

        await this.Runner(
            new RecordingDispatcher(m => m.Payload.Contains("false") ? throw new InvalidOperationException() : Task.CompletedTask),
            new ManualClock(),
            o => o.MaxAttempts = 1).DrainOnce(Ct);

        Assert.Equal(2, Interlocked.Read(ref dispatched));
        Assert.Equal(1, Interlocked.Read(ref deadLettered));
    }

    // ── Pinned storage shape ────────────────────────────────────────────

    [Fact]
    public async Task ColumnNames_ArePinnedUnderAHostileNamingConvention()
    {
        var hostileDatabase = await provider.CreateDatabase<HostileDbContext>(o => new HostileDbContext(o));
        await using var db = new HostileDbContext(provider.Options<HostileDbContext>(hostileDatabase));

        // The convention is live (it renamed the business table) ...
        Assert.Equal("X_CUSTOMER", db.Model.FindEntityType(typeof(Customer))!.GetTableName());
        Assert.Equal("X_NAME", db.Model.FindEntityType(typeof(Customer))!.FindProperty(nameof(Customer.Name))!.GetColumnName());

        // ... and the outbox ignored it.
        var outbox = db.Model.FindEntityType(typeof(OutboxMessage))!;
        Assert.Equal(OutboxModelBuilderExtensions.DefaultTableName, outbox.GetTableName());
        Assert.Equal(OutboxColumns.All.Order(), outbox.GetProperties().Select(p => p.GetColumnName()).Order());

        db.EnqueueRaw("pinned", "{}", "p");
        await db.SaveChangesAsync(Ct);

        // A reader with no CLR type addresses the rows by the pinned names, unquoted, on every provider.
        var connection = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(Ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {string.Join(", ", OutboxColumns.All)} FROM outbox";
            await using var reader = await command.ExecuteReaderAsync(Ct);
            Assert.True(await reader.ReadAsync(Ct));
            Assert.Equal("pinned", reader.GetString(reader.GetOrdinal(OutboxColumns.MessageType)));
            Assert.Equal("p", reader.GetString(reader.GetOrdinal(OutboxColumns.PartitionKey)));
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }

        var dispatcher = new RecordingDispatcher();
        await new OutboxRunner(() => new HostileDbContext(provider.Options<HostileDbContext>(hostileDatabase)), dispatcher, null, new ManualClock()).DrainOnce(Ct);
        Assert.Single(dispatcher.Dispatched);
    }

    // ── Watch ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Watch_YieldsExistingThenNewMessages_AndStopsOnCancel()
    {
        await this.EnqueueMany(2);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        await using var watcher = this.Context();

        var seen = new List<OutboxMessage>();
        await foreach (var message in watcher.WatchOutbox(o => o.PollInterval = TimeSpan.FromMilliseconds(50), cts.Token))
        {
            seen.Add(message);
            if (seen.Count == 2)
                await this.EnqueueMany(1);
            if (seen.Count == 3)
                await cts.CancelAsync();
        }

        Assert.Equal(3, seen.Count);
        Assert.Equal(3, seen.Select(m => m.Id).Distinct().Count());
        Assert.False(Ct.IsCancellationRequested);
    }

    [Fact]
    public async Task Watch_WithoutIncludeExisting_SkipsTheBacklog()
    {
        var backlog = await this.EnqueueMany(2);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        await using var watcher = this.Context();

        var stream = watcher.WatchOutbox(o =>
        {
            o.IncludeExisting = false;
            o.PollInterval = TimeSpan.FromMilliseconds(50);
        }, cts.Token);

        var enqueued = Task.Run(async () =>
        {
            await Task.Delay(300, cts.Token);
            return await this.EnqueueMany(1);
        }, cts.Token);

        OutboxMessage? first = null;
        await foreach (var message in stream)
        {
            first = message;
            await cts.CancelAsync();
        }

        Assert.NotNull(first);
        Assert.DoesNotContain(first!.Id, backlog.Select(m => m.Id));
        Assert.Equal((await enqueued).Single().Id, first.Id);
    }

    [Fact]
    public async Task Watch_DeadLetters_DecodedByEventType()
    {
        await using (var db = this.Context())
        {
            db.Enqueue(new CustomerCreated(Guid.NewGuid(), "dead"));
            db.EnqueueRaw("other", "{}");
            await db.SaveChangesAsync(Ct);
        }
        await this.Runner(new RecordingDispatcher(_ => throw new InvalidOperationException()), new ManualClock(), o => o.MaxAttempts = 1).DrainOnce(Ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        await using var watcher = this.Context();

        await foreach (var envelope in watcher.WatchOutbox<CustomerCreated>(o => o.States = OutboxStates.DeadLettered, cts.Token))
        {
            Assert.Equal("dead", envelope.Event.Name);
            Assert.NotNull(envelope.Message.DeadLetteredAt);
            await cts.CancelAsync();
        }
        Assert.True(cts.IsCancellationRequested);
    }

    [Fact]
    public async Task Watch_YieldsEachRevisionOnce()
    {
        var message = Assert.Single(await this.EnqueueMany(1));
        var clock = new ManualClock();
        await using var watcher = this.Context();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        var revisions = new List<int>();
        await foreach (var m in watcher.WatchOutbox(o =>
        {
            o.States = OutboxStates.All;
            o.PollInterval = TimeSpan.FromMilliseconds(50);
        }, cts.Token))
        {
            revisions.Add(m.Version);
            if (revisions.Count == 1)
                await this.Runner(new RecordingDispatcher(), clock).DrainOnce(Ct);   // claim + ack = version 2
            if (m.Version == 2)
                await cts.CancelAsync();
        }

        Assert.Equal(0, revisions[0]);
        Assert.Equal(2, revisions[^1]);
        Assert.Equal(revisions.Count, revisions.Distinct().Count());
        Assert.Equal(message.Id, (await this.Messages()).Single().Id);
    }
}
