using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Shiny.EntityFrameworkCore.Outbox.Tests;

/// <summary>Provider-independent behaviour: id generation, configuration validation, and the unsupported-setup guards.</summary>
public sealed class OutboxGuardTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static TestDbContext InMemory(Action<DbContextOptionsBuilder<TestDbContext>>? configure = null)
    {
        var builder = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
        configure?.Invoke(builder);
        return new TestDbContext(builder.Options);
    }

    static NoOutboxDbContext Unmapped()
        => new(new DbContextOptionsBuilder<NoOutboxDbContext>().UseSqlite("Data Source=:memory:").Options);

    // ── Ids ─────────────────────────────────────────────────────────────

    [Fact]
    public void OutboxId_IsMonotonicVersion7InNForm()
    {
        var ids = Enumerable.Range(0, 10_000).Select(_ => OutboxId.Next()).ToList();

        Assert.All(ids, id => Assert.Matches("^[0-9a-f]{32}$", id));
        Assert.Equal(ids, ids.Order(StringComparer.Ordinal));
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(ids.Take(10), id => Assert.Equal(7, Guid.ParseExact(id, "N").Version));
    }

    [Fact]
    public async Task OutboxId_IsMonotonicAcrossThreads()
    {
        var batches = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => Enumerable.Range(0, 2_000).Select(_ => OutboxId.Next()).ToList(), Ct)));
        var all = batches.SelectMany(b => b).ToList();

        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.All(batches, b => Assert.Equal(b, b.Order(StringComparer.Ordinal)));
    }

    // ── Model ───────────────────────────────────────────────────────────

    [Fact]
    public void AddOutbox_MapsPinnedColumnsConcurrencyTokenAndPollIndex()
    {
        using var db = new TestDbContext(new DbContextOptionsBuilder<TestDbContext>().UseSqlite("Data Source=:memory:").Options);
        var entity = db.Model.FindEntityType(typeof(OutboxMessage))!;

        Assert.Equal("outbox", entity.GetTableName());
        Assert.True(entity.FindProperty(nameof(OutboxMessage.Version))!.IsConcurrencyToken);
        Assert.Equal(32, entity.FindProperty(nameof(OutboxMessage.Id))!.GetMaxLength());
        var index = Assert.Single(entity.GetIndexes());
        Assert.Equal(
            [nameof(OutboxMessage.ProcessedAt), nameof(OutboxMessage.DeadLetteredAt), nameof(OutboxMessage.AvailableAt)],
            index.Properties.Select(p => p.Name));
        Assert.Equal("ix_outbox_poll", index.GetDatabaseName());
    }

    [Fact]
    public void AddOutbox_HonoursTableAndSchema()
    {
        var builder = new ModelBuilder();
        builder.AddOutbox("events", "messaging");
        var entity = builder.Model.FindEntityType(typeof(OutboxMessage))!;
        Assert.Equal("events", entity.GetTableName());
        Assert.Equal("messaging", entity.GetSchema());
        Assert.Throws<ArgumentException>(() => new ModelBuilder().AddOutbox(" "));
    }

    // ── Enqueue guards ──────────────────────────────────────────────────

    [Fact]
    public void Enqueue_WithoutAddOutbox_SaysWhatToCall()
    {
        using var db = Unmapped();
        var error = Assert.Throws<InvalidOperationException>(() => db.EnqueueRaw("x", "{}"));
        Assert.Contains("AddOutbox", error.Message);
    }

    [Fact]
    public void Enqueue_OnTheInMemoryProvider_IsRefused()
    {
        using var db = InMemory();
        var error = Assert.Throws<NotSupportedException>(() => db.Enqueue(new CustomerCreated(Guid.NewGuid(), "x")));
        Assert.Contains("relational", error.Message);
    }

    [Fact]
    public void EnqueueRaw_ValidatesArguments()
    {
        using var db = Unmapped();
        Assert.Throws<ArgumentException>(() => db.EnqueueRaw(" ", "{}"));
        Assert.Throws<ArgumentNullException>(() => db.EnqueueRaw("x", null!));
        Assert.Throws<ArgumentNullException>(() => OutboxDbContextExtensions.EnqueueRaw(null!, "x", "{}"));
    }

    // ── Publishing guards ───────────────────────────────────────────────

    [Fact]
    public async Task Publishing_OnTheInMemoryProvider_IsRefused()
    {
        await using var db = InMemory(o => o.AddOutboxPublishing(p => p.Publish<Customer>(c => new CustomerCreated(c.Entity.Id, c.Entity.Name))));
        db.Customers.Add(new Customer { Id = Guid.NewGuid() });
        await Assert.ThrowsAsync<NotSupportedException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task Publishing_IsInertForUnmappedTypes()
    {
        // No registration matches Order, so the in-memory save is not refused — the guard fires only when a message
        // would actually be written.
        await using var db = InMemory(o => o.AddOutboxPublishing(p => p.Publish<Customer>(c => new CustomerCreated(c.Entity.Id, c.Entity.Name))));
        db.Orders.Add(new Order { Id = 1 });
        Assert.Equal(1, await db.SaveChangesAsync(Ct));
    }

    [Fact]
    public void AddOutboxPublishing_ValidatesItsRegistrations()
    {
        var builder = new DbContextOptionsBuilder<TestDbContext>();
        Assert.Throws<ArgumentException>(() => builder.AddOutboxPublishing(_ => { }));
        Assert.Throws<ArgumentException>(() => builder.AddOutboxPublishing(p => p.Publish<Order>(_ => null, OutboxOperations.None)));
        Assert.Throws<ArgumentNullException>(() => builder.AddOutboxPublishing(p => p.Publish<Order>(null!)));
        Assert.Throws<ArgumentNullException>(() => builder.AddOutboxPublishing(null!));
    }

    // ── Options and registration ────────────────────────────────────────

    public static TheoryData<string> InvalidOptions => ["BatchSize", "MaxParallelism", "MaxAttempts", "PollInterval", "Retention", "RetentionInterval", "Backoff", "PayloadSerializerOptions"];

    static void Break(OutboxOptions o, string name)
    {
        switch (name)
        {
            case "BatchSize": o.BatchSize = 0; break;
            case "MaxParallelism": o.MaxParallelism = 0; break;
            case "MaxAttempts": o.MaxAttempts = 0; break;
            case "PollInterval": o.PollInterval = TimeSpan.Zero; break;
            case "Retention": o.Retention = TimeSpan.Zero; break;
            case "RetentionInterval": o.RetentionInterval = TimeSpan.Zero; break;
            case "Backoff": o.Backoff = null!; break;
            default: o.PayloadSerializerOptions = null!; break;
        }
    }

    [Theory]
    [MemberData(nameof(InvalidOptions))]
    public void InvalidOptions_FailAtRegistrationAndConstruction(string option)
    {
        var services = new ServiceCollection();
        var error = Assert.ThrowsAny<ArgumentException>(() => services.AddDbContextOutbox<TestDbContext>((_, _) => Task.CompletedTask, o => Break(o, option)));
        Assert.Contains(option, error.Message);

        var options = new OutboxOptions();
        Break(options, option);
        Assert.ThrowsAny<ArgumentException>(() => new OutboxRunner(() => Unmapped(), new RecordingDispatcher(), options));
    }

    [Fact]
    public void AddDbContextOutbox_TwiceForOneContext_Throws()
    {
        var services = new ServiceCollection();
        services.AddDbContextOutbox<TestDbContext>((_, _) => Task.CompletedTask);
        Assert.Throws<InvalidOperationException>(() => services.AddDbContextOutbox<TestDbContext, RecordingDispatcher>());
    }

    [Fact]
    public void AddDbContextOutbox_PerContextRegistrationsAreKeyed()
    {
        var services = new ServiceCollection();
        services.AddDbContextOutbox<TestDbContext>((_, _) => Task.CompletedTask, o => o.BatchSize = 7);
        services.AddDbContextOutbox<NoOutboxDbContext>((_, _) => Task.CompletedTask, o => o.BatchSize = 9);

        using var sp = services.BuildServiceProvider();
        Assert.Equal(7, sp.GetRequiredKeyedService<OutboxRunner>(typeof(TestDbContext)).Options.BatchSize);
        Assert.Equal(9, sp.GetRequiredKeyedService<OutboxRunner>(typeof(NoOutboxDbContext)).Options.BatchSize);
        Assert.Equal(7, sp.GetRequiredService<OutboxRunner>().Options.BatchSize);    // unkeyed = the first
        Assert.Equal(2, sp.GetServices<IHostedService>().Count());
    }

    [Fact]
    public void Runner_ValidatesArguments()
    {
        Assert.Throws<ArgumentNullException>(() => new OutboxRunner(null!, new RecordingDispatcher()));
        Assert.Throws<ArgumentNullException>(() => new OutboxRunner(() => Unmapped(), (IOutboxDispatcher)null!));
        Assert.Throws<ArgumentNullException>(() => new OutboxRunner(() => Unmapped(), (IServiceScopeFactory)null!));
        Assert.Throws<ArgumentNullException>(() => new OutboxAdmin(null!));
    }

    [Fact]
    public async Task HostedProcessor_FailsStartupWithoutAnOutboxMapping()
    {
        var services = new ServiceCollection();
        services.AddDbContext<NoOutboxDbContext>(o => o.UseSqlite("Data Source=:memory:"));
        services.AddDbContextOutbox<NoOutboxDbContext>((_, _) => Task.CompletedTask);

        await using var sp = services.BuildServiceProvider();
        var hosted = Assert.Single(sp.GetServices<IHostedService>());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => hosted.StartAsync(Ct));
        Assert.Contains("AddOutbox", error.Message);
    }

    [Fact]
    public async Task HostedProcessor_FailsStartupOnTheInMemoryProvider()
    {
        var services = new ServiceCollection();
        services.AddDbContext<TestDbContext>(o => o.UseInMemoryDatabase("processor-guard"));
        services.AddDbContextOutbox<TestDbContext>((_, _) => Task.CompletedTask);

        await using var sp = services.BuildServiceProvider();
        var hosted = Assert.Single(sp.GetServices<IHostedService>());
        await Assert.ThrowsAsync<NotSupportedException>(() => hosted.StartAsync(Ct));
    }

    [Fact]
    public async Task Watch_ValidatesEagerly()
    {
        await using var db = new TestDbContext(new DbContextOptionsBuilder<TestDbContext>().UseSqlite("Data Source=:memory:").Options);
        Assert.Throws<ArgumentException>(() => db.WatchOutbox(o => o.States = 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => db.WatchOutbox(o => o.PollInterval = TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => db.WatchOutbox(o => o.BatchSize = 0));
        Assert.Throws<ArgumentNullException>(() => db.WatchOutbox<CustomerCreated>((System.Text.Json.Serialization.Metadata.JsonTypeInfo<CustomerCreated>)null!));

        await using var unmapped = Unmapped();
        Assert.Throws<InvalidOperationException>(() => unmapped.WatchOutbox());
    }
}
