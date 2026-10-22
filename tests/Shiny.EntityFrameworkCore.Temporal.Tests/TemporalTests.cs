using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Shiny.EntityFrameworkCore.Temporal.Tests;

public sealed class SqliteTemporalTests(SqliteFixture fixture) : TemporalTests(fixture), IClassFixture<SqliteFixture>;

public sealed class PostgresTemporalTests(PostgresFixture fixture) : TemporalTests(fixture), IClassFixture<PostgresFixture>;

public sealed class SqlServerTemporalTests(SqlServerFixture fixture) : TemporalTests(fixture), IClassFixture<SqlServerFixture>;

public sealed class MySqlTemporalTests(MySqlFixture fixture) : TemporalTests(fixture), IClassFixture<MySqlFixture>;

/// <summary>The provider matrix: every test runs against SQLite, PostgreSQL, SQL Server and MySQL.</summary>
public abstract class TemporalTests(ProviderFixture fixture) : IAsyncLifetime
{
    protected readonly ManualClock Clock = new();
    readonly string database = "tm" + Guid.NewGuid().ToString("N")[..16];

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using var db = this.Create();
        await db.Database.EnsureCreatedAsync(Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    TestDbContext Create(
        Action<TemporalHistoryOptions>? history = null,
        HistoryOptions? product = null,
        bool retrying = false,
        bool withHistory = true,
        params IInterceptor[] interceptors)
    {
        var builder = new DbContextOptionsBuilder<TestDbContext>();
        this.Configure(builder, retrying, withHistory, history);
        if (interceptors.Length > 0)
            builder.AddInterceptors(interceptors);
        return new TestDbContext(builder.Options) { ProductHistory = product };
    }

    void Configure(DbContextOptionsBuilder builder, bool retrying, bool withHistory, Action<TemporalHistoryOptions>? history)
    {
        fixture.Use(builder, this.database, retrying);
        builder.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
        builder.ReplaceService<IModelCacheKeyFactory, ProductHistoryModelCacheKeyFactory>();
        if (withHistory)
        {
            builder.UseEntityHistory(o =>
            {
                o.TimeProvider = this.Clock;
                history?.Invoke(o);
            });
        }
    }

    async Task<List<EntityHistoryRecord>> AllHistory()
    {
        await using var db = this.Create();
        return await db.Set<EntityHistoryRecord>().AsNoTracking().ToListAsync(Ct);
    }

    async Task<Order> AddOrder(Action<Order>? configure = null)
    {
        await using var db = this.Create();
        var order = new Order
        {
            Customer = "Ada",
            Quantity = 1,
            Price = new Money { Amount = 10m, Currency = "USD" },
            ShippingAddress = new Address { Street = "1 Main", City = "Springfield" },
            Lines = [new OrderLine { Product = "Widget", Qty = 1 }]
        };
        configure?.Invoke(order);
        db.Orders.Add(order);
        await db.SaveChangesAsync(Ct);
        return order;
    }

    async Task Mutate<T>(object key, Action<T> change) where T : class
    {
        await using var db = this.Create();
        var entity = await db.FindAsync<T>(key is object[] k ? k : new[] { key }, Ct);
        change(entity!);
        await db.SaveChangesAsync(Ct);
    }

    async Task Remove<T>(object key) where T : class
    {
        await using var db = this.Create();
        var entity = await db.FindAsync<T>(key is object[] k ? k : new[] { key }, Ct);
        db.Remove(entity!);
        await db.SaveChangesAsync(Ct);
    }

    async Task AddProduct(string sku, string name, string? category = null)
    {
        await using var db = this.Create();
        var entry = db.Products.Add(new Product { Sku = sku, Name = name });
        entry.Property("Category").CurrentValue = category;
        await db.SaveChangesAsync(Ct);
    }

    // ── Versions ─────────────────────────────────────────────────────────

    [Fact]
    public async Task InsertUpdateDelete_RecordsChainedVersions()
    {
        var t0 = this.Clock.GetUtcNow();
        await this.AddProduct("A", "One");
        var t1 = this.Clock.Advance(TimeSpan.FromMinutes(1));
        await this.Mutate<Product>("A", p => p.Name = "Two");
        var t2 = this.Clock.Advance(TimeSpan.FromMinutes(1));
        await this.Remove<Product>("A");

        await using var db = this.Create();
        var history = await db.HistoryAsync<Product>("A", Ct);

        Assert.Equal([1L, 2L, 3L], history.Select(v => v.Version));
        Assert.Equal([TemporalOperation.Inserted, TemporalOperation.Updated, TemporalOperation.Removed], history.Select(v => v.Operation));
        Assert.All(history, v => Assert.Equal("A", v.Key));

        Assert.Equal(t0, history[0].ValidFrom);
        Assert.Equal(t1, history[0].ValidTo);
        Assert.Equal(t1, history[1].ValidFrom);
        Assert.Equal(t2, history[1].ValidTo);
        Assert.Equal(t2, history[2].ValidFrom);
        Assert.Null(history[2].ValidTo);

        Assert.Equal("One", history[0].Entity!.Name);
        Assert.Equal("Two", history[1].Entity!.Name);
        Assert.Null(history[2].Entity);

        // Materialized versions are detached.
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task SeveralSavesOnOneContext_IncrementVersions_SyncAndAsync()
    {
        await using (var db = this.Create())
        {
            var product = new Product { Sku = "S", Name = "v1" };
            db.Products.Add(product);
            db.SaveChanges();
            this.Clock.Advance(TimeSpan.FromSeconds(1));
            product.Name = "v2";
            await db.SaveChangesAsync(Ct);
            this.Clock.Advance(TimeSpan.FromSeconds(1));
            product.Name = "v3";
            db.SaveChanges();
        }

        await using var read = this.Create();
        var history = await read.HistoryAsync<Product>("S", Ct);
        Assert.Equal(["v1", "v2", "v3"], history.Select(v => v.Entity!.Name));
        Assert.Single(history, v => v.ValidTo == null);
    }

    [Fact]
    public async Task UnmappedEntities_WriteNoHistory_AndCantBeRead()
    {
        await using (var db = this.Create())
        {
            db.Notes.Add(new Note { Text = "hi" });
            await db.SaveChangesAsync(Ct);
        }

        Assert.Empty(await this.AllHistory());

        await using var read = this.Create();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => read.HistoryAsync<Note>(1, Ct));
        Assert.Contains("HasHistory", ex.Message);
    }

    [Fact]
    public async Task WithoutUseEntityHistory_NothingIsRecorded_AndReadsThrow()
    {
        await using (var db = this.Create(withHistory: false))
        {
            db.Products.Add(new Product { Sku = "X", Name = "untracked" });
            await db.SaveChangesAsync(Ct);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => db.HistoryAsync<Product>("X", Ct));
            Assert.Contains("UseEntityHistory", ex.Message);
        }

        Assert.Empty(await this.AllHistory());
    }

    // ── Point in time ────────────────────────────────────────────────────

    [Fact]
    public async Task AsOf_HonorsValidityBoundaries()
    {
        var t0 = this.Clock.GetUtcNow();
        await this.AddProduct("A", "One");
        var t1 = this.Clock.Advance(TimeSpan.FromMinutes(1));
        await this.Mutate<Product>("A", p => p.Name = "Two");
        var t2 = this.Clock.Advance(TimeSpan.FromMinutes(1));
        await this.Remove<Product>("A");

        await using var db = this.Create();
        var micro = TimeSpan.FromTicks(10);

        Assert.Null(await db.AsOfAsync<Product>("A", t0 - TimeSpan.FromTicks(1), Ct));
        Assert.Equal("One", (await db.AsOfAsync<Product>("A", t0, Ct))!.Name);
        Assert.Equal("One", (await db.AsOfAsync<Product>("A", t1 - micro, Ct))!.Name);
        Assert.Equal("Two", (await db.AsOfAsync<Product>("A", t1, Ct))!.Name);
        Assert.Equal("Two", (await db.AsOfAsync<Product>("A", t2 - TimeSpan.FromTicks(1), Ct))!.Name);
        Assert.Null(await db.AsOfAsync<Product>("A", t2, Ct));
        Assert.Null(await db.AsOfAsync<Product>("A", t2.AddDays(1), Ct));
        Assert.Null(await db.AsOfAsync<Product>("missing", t1, Ct));
    }

    [Fact]
    public async Task AsOfAll_ExcludesRemovedAndNotYetCreated()
    {
        var t0 = this.Clock.GetUtcNow();
        await this.AddProduct("A", "a");
        var t1 = this.Clock.Advance(TimeSpan.FromMinutes(1));
        await this.AddProduct("B", "b");
        var t2 = this.Clock.Advance(TimeSpan.FromMinutes(1));
        await this.Remove<Product>("A");
        var t3 = this.Clock.Advance(TimeSpan.FromMinutes(1));
        await this.AddProduct("C", "c");
        await this.Mutate<Product>("C", p => p.Name = "c2");

        await using var db = this.Create();
        Assert.Empty(await db.AsOfAllAsync<Product>(t0.AddTicks(-10), Ct));
        Assert.Equal(["A"], (await db.AsOfAllAsync<Product>(t0, Ct)).Select(p => p.Sku));
        Assert.Equal(["A", "B"], (await db.AsOfAllAsync<Product>(t1, Ct)).Select(p => p.Sku).Order());
        Assert.Equal(["B"], (await db.AsOfAllAsync<Product>(t2, Ct)).Select(p => p.Sku));

        var latest = await db.AsOfAllAsync<Product>(t3, Ct);
        Assert.Equal(["B", "C"], latest.Select(p => p.Sku).Order());
        Assert.Equal("c2", latest.Single(p => p.Sku == "C").Name);
    }

    [Fact]
    public async Task ChangesBetween_IsHalfOpen()
    {
        var t0 = this.Clock.GetUtcNow();
        await this.AddProduct("A", "a");
        var t1 = this.Clock.Advance(TimeSpan.FromMinutes(1));
        await this.Mutate<Product>("A", p => p.Name = "a2");
        var t2 = this.Clock.Advance(TimeSpan.FromMinutes(1));
        await this.AddProduct("B", "b");

        await using var db = this.Create();

        var first = await db.ChangesBetweenAsync<Product>(t0, t1, Ct);
        Assert.Equal([("A", 1L)], first.Select(v => (v.Key, v.Version)));

        var rest = await db.ChangesBetweenAsync<Product>(t1, t2.AddTicks(1), Ct);
        Assert.Equal([("A", 2L), ("B", 1L)], rest.Select(v => (v.Key, v.Version)));

        Assert.Empty(await db.ChangesBetweenAsync<Product>(t0, t0, Ct));
        Assert.Equal(3, (await db.ChangesBetweenAsync<Product>(t0.AddTicks(-1), t2.AddTicks(1), Ct)).Count);
        await Assert.ThrowsAsync<ArgumentException>(() => db.ChangesBetweenAsync<Product>(t1, t0, Ct));
    }

    // ── Actors ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ChangesByActor_UsesCaptureActor()
    {
        string? actor = "alice";
        await using (var db = this.Create(h => h.CaptureActor = () => actor))
        {
            db.Products.Add(new Product { Sku = "A", Name = "a" });
            await db.SaveChangesAsync(Ct);

            this.Clock.Advance(TimeSpan.FromSeconds(1));
            actor = "bob";
            (await db.Products.FindAsync(["A"], Ct))!.Name = "a2";
            await db.SaveChangesAsync(Ct);

            this.Clock.Advance(TimeSpan.FromSeconds(1));
            actor = "alice";
            db.Products.Add(new Product { Sku = "B", Name = "b" });
            await db.SaveChangesAsync(Ct);
        }

        await using var read = this.Create();
        var alice = await read.ChangesByActorAsync<Product>("alice", Ct);
        Assert.Equal([("A", 1L), ("B", 1L)], alice.Select(v => (v.Key, v.Version)));
        Assert.All(alice, v => Assert.Equal("alice", v.Actor));

        var bob = await read.ChangesByActorAsync<Product>("bob", Ct);
        Assert.Equal([("A", 2L)], bob.Select(v => (v.Key, v.Version)));
        Assert.Empty(await read.ChangesByActorAsync<Product>("carol", Ct));
    }

    [Fact]
    public async Task ResolveActor_UsesTheRequestScope_AndFallsBackToCaptureActor()
    {
        var services = new ServiceCollection();
        services.AddScoped<CurrentUser>();
        services.AddDbContext<TestDbContext>(o => this.Configure(o, false, true, h =>
        {
            h.ResolveActor = sp => sp.GetRequiredService<CurrentUser>().Name;
            h.CaptureActor = () => "fallback";
        }));
        await using var provider = services.BuildServiceProvider(validateScopes: true);

        await using (var scope = provider.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<CurrentUser>().Name = "alice";
            var db = scope.ServiceProvider.GetRequiredService<TestDbContext>();
            db.Products.Add(new Product { Sku = "A", Name = "a" });
            await db.SaveChangesAsync(Ct);
        }

        this.Clock.Advance(TimeSpan.FromSeconds(1));
        await using (var scope = provider.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<CurrentUser>().Name = "bob";
            var db = scope.ServiceProvider.GetRequiredService<TestDbContext>();
            (await db.Products.FindAsync(["A"], Ct))!.Name = "a2";
            await db.SaveChangesAsync(Ct);
        }

        // A context built by hand has no application service provider, so CaptureActor answers.
        this.Clock.Advance(TimeSpan.FromSeconds(1));
        await using (var db = this.Create(h =>
        {
            h.ResolveActor = sp => sp.GetRequiredService<CurrentUser>().Name;
            h.CaptureActor = () => "fallback";
        }))
        {
            (await db.Products.FindAsync(["A"], Ct))!.Name = "a3";
            await db.SaveChangesAsync(Ct);
        }

        await using var read = this.Create();
        var history = await read.HistoryAsync<Product>("A", Ct);
        Assert.Equal(["alice", "bob", "fallback"], history.Select(v => v.Actor));
        Assert.Equal(["A"], (await read.ChangesByActorAsync<Product>("bob", Ct)).Select(v => v.Key));
    }

    // ── Restore ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Restore_ExistingEntity_WritesANewVersion_IncludingShadowValues()
    {
        await this.AddProduct("A", "One", "Tools");
        this.Clock.Advance(TimeSpan.FromMinutes(1));
        await using (var db = this.Create())
        {
            var product = await db.Products.FindAsync(["A"], Ct);
            product!.Name = "Two";
            db.Entry(product).Property("Category").CurrentValue = "Toys";
            await db.SaveChangesAsync(Ct);
        }

        this.Clock.Advance(TimeSpan.FromMinutes(1));
        await using (var db = this.Create())
        {
            var restored = await db.RestoreAsync<Product>("A", 1, Ct);
            Assert.NotNull(restored);
            Assert.Equal("One", restored.Name);
            Assert.Equal(EntityState.Unchanged, db.Entry(restored).State);
        }

        await using var read = this.Create();
        var live = await read.Products.SingleAsync(p => p.Sku == "A", Ct);
        Assert.Equal("One", live.Name);
        Assert.Equal("Tools", read.Entry(live).Property("Category").CurrentValue);

        var history = await read.HistoryAsync<Product>("A", Ct);
        Assert.Equal([TemporalOperation.Inserted, TemporalOperation.Updated, TemporalOperation.Updated], history.Select(v => v.Operation));
        Assert.Equal(["One", "Two", "One"], history.Select(v => v.Entity!.Name));
    }

    [Fact]
    public async Task Restore_AfterDelete_ReinsertsWithTheOriginalIdentityKey()
    {
        var order = await this.AddOrder();
        this.Clock.Advance(TimeSpan.FromMinutes(1));
        await this.Mutate<Order>(order.Id, o =>
        {
            o.Status = "Shipped";
            o.Price.Amount = 12m;
            o.ShippingAddress!.City = "Shelbyville";
            o.Lines.Add(new OrderLine { Product = "Gadget", Qty = 2 });
        });
        this.Clock.Advance(TimeSpan.FromMinutes(1));
        await this.Remove<Order>(order.Id);
        this.Clock.Advance(TimeSpan.FromMinutes(1));

        await using (var db = this.Create())
        {
            var restored = await db.RestoreAsync<Order>(order.Id, 2, Ct);
            Assert.NotNull(restored);
            Assert.Equal(order.Id, restored.Id);
        }

        await using var read = this.Create();
        var live = await read.Orders.SingleAsync(o => o.Id == order.Id, Ct);
        Assert.Equal("Shipped", live.Status);
        Assert.Equal(12m, live.Price.Amount);
        Assert.Equal("Shelbyville", live.ShippingAddress!.City);
        Assert.Equal(["Gadget", "Widget"], live.Lines.Select(l => l.Product).Order());

        var history = await read.HistoryAsync<Order>(order.Id, Ct);
        Assert.Equal(
            [TemporalOperation.Inserted, TemporalOperation.Updated, TemporalOperation.Removed, TemporalOperation.Inserted],
            history.Select(v => v.Operation));
        Assert.Equal([1L, 2L, 3L, 4L], history.Select(v => v.Version));
        Assert.NotNull(history[2].ValidTo);
        Assert.Null(history[3].ValidTo);

        // A new identity value still comes after the restored one.
        var next = await this.AddOrder();
        Assert.NotEqual(order.Id, next.Id);
    }

    [Fact]
    public async Task Restore_ExistingEntityWithOwnedAndComplexValues_ReplacesThem()
    {
        var order = await this.AddOrder();
        this.Clock.Advance(TimeSpan.FromMinutes(1));
        await this.Mutate<Order>(order.Id, o =>
        {
            o.Price = new Money { Amount = 99m, Currency = "EUR" };
            o.ShippingAddress = null;
            o.Lines.Clear();
        });

        await using (var db = this.Create())
            Assert.NotNull(await db.RestoreAsync<Order>(order.Id, 1, Ct));

        await using var read = this.Create();
        var live = await read.Orders.SingleAsync(o => o.Id == order.Id, Ct);
        Assert.Equal(10m, live.Price.Amount);
        Assert.Equal("USD", live.Price.Currency);
        Assert.Equal("Springfield", live.ShippingAddress!.City);
        Assert.Equal(["Widget"], live.Lines.Select(l => l.Product));
    }

    [Fact]
    public async Task Restore_MissingVersionOrTombstone_ReturnsNull()
    {
        await this.AddProduct("A", "One");
        await this.Remove<Product>("A");

        await using var db = this.Create();
        Assert.Null(await db.RestoreAsync<Product>("A", 2, Ct));   // tombstone
        Assert.Null(await db.RestoreAsync<Product>("A", 42, Ct));  // never existed
        Assert.Null(await db.RestoreAsync<Product>("B", 1, Ct));
        Assert.Equal(2, (await db.HistoryAsync<Product>("A", Ct)).Count);
    }

    // ── Diff ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetDiffBetween_ProducesAnRfc6902Patch()
    {
        var order = await this.AddOrder();
        await this.Mutate<Order>(order.Id, o =>
        {
            o.Status = "Shipped";
            o.Price.Amount = 12m;
            o.ShippingAddress!.City = "Shelbyville";
            o.Lines.Add(new OrderLine { Product = "Gadget", Qty = 2 });
        });
        await this.Remove<Order>(order.Id);

        await using var db = this.Create();
        var patch = await db.GetDiffBetweenAsync<Order>(order.Id, 1, 2, Ct);
        Assert.NotNull(patch);

        var ops = patch.Operations.ToDictionary(o => o.Path);
        Assert.Equal(["/Lines", "/Price/Amount", "/ShippingAddress/City", "/Status"], ops.Keys.Order());
        Assert.All(ops.Values, o => Assert.Equal("replace", o.Op));
        Assert.Equal("Shipped", ops["/Status"].Value!.Value.GetString());
        Assert.Equal(12m, ops["/Price/Amount"].Value!.Value.GetDecimal());
        Assert.Equal("Shelbyville", ops["/ShippingAddress/City"].Value!.Value.GetString());
        Assert.Equal(2, ops["/Lines"].Value!.Value.GetArrayLength());

        // The patch replays version 1 into version 2.
        var v1 = (await db.HistoryAsync<Order>(order.Id, Ct))[0].Entity!;
        var patched = patch.ApplyTo(v1);
        Assert.Equal("Shipped", patched.Status);
        Assert.Equal("Shelbyville", patched.ShippingAddress!.City);
        Assert.Equal(2, patched.Lines.Count);

        using var json = JsonDocument.Parse(patch.ToJson());
        Assert.Equal(4, json.RootElement.GetArrayLength());
        Assert.All(json.RootElement.EnumerateArray(), e => Assert.Equal("replace", e.GetProperty("op").GetString()));

        Assert.Empty((await db.GetDiffBetweenAsync<Order>(order.Id, 2, 2, Ct))!.Operations);
        Assert.Null(await db.GetDiffBetweenAsync<Order>(order.Id, 2, 3, Ct));  // 3 is the tombstone
        Assert.Null(await db.GetDiffBetweenAsync<Order>(order.Id, 1, 9, Ct));
    }

    // ── Retention ────────────────────────────────────────────────────────

    [Fact]
    public async Task Retention_PrunesClosedVersionsOlderThanTheWindow()
    {
        var product = new HistoryOptions { Retention = TimeSpan.FromMinutes(10) };
        await using (var db = this.Create(product: product))
        {
            var p = new Product { Sku = "A", Name = "v1" };
            db.Products.Add(p);
            await db.SaveChangesAsync(Ct);

            this.Clock.Advance(TimeSpan.FromMinutes(5));
            p.Name = "v2";
            await db.SaveChangesAsync(Ct);               // v1 closes at +5

            this.Clock.Advance(TimeSpan.FromMinutes(15));
            p.Name = "v3";
            await db.SaveChangesAsync(Ct);               // cutoff +10: v1 (closed +5) goes, v2 (closed +20) stays
        }

        await using var read = this.Create(product: product);
        Assert.Equal([2L, 3L], (await read.HistoryAsync<Product>("A", Ct)).Select(v => v.Version));
    }

    [Fact]
    public async Task MaxVersions_KeepsTheNewestN_PerEntity()
    {
        var product = new HistoryOptions { MaxVersions = 2 };
        await using (var db = this.Create(product: product))
        {
            var a = new Product { Sku = "A", Name = "a1" };
            var b = new Product { Sku = "B", Name = "b1" };
            db.Products.AddRange(a, b);
            await db.SaveChangesAsync(Ct);
            for (var i = 2; i <= 4; i++)
            {
                this.Clock.Advance(TimeSpan.FromSeconds(1));
                a.Name = "a" + i;
                await db.SaveChangesAsync(Ct);
            }
        }

        await using var read = this.Create(product: product);
        var history = await read.HistoryAsync<Product>("A", Ct);
        Assert.Equal([3L, 4L], history.Select(v => v.Version));
        Assert.Null(history[^1].ValidTo);
        Assert.Equal([1L], (await read.HistoryAsync<Product>("B", Ct)).Select(v => v.Version));
    }

    // ── Keys ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task IdentityKeys_AreRecordedAfterTheInsert()
    {
        await using (var db = this.Create())
        {
            db.Orders.AddRange(new Order { Customer = "a" }, new Order { Customer = "b" }, new Order { Customer = "c" });
            await db.SaveChangesAsync(Ct);
        }

        await using var read = this.Create();
        var ids = await read.Orders.Select(o => o.Id).ToListAsync(Ct);
        var history = await this.AllHistory();
        Assert.Equal(ids.Select(i => i.ToString()).Order(), history.Select(h => h.EntityKey).Order());
        Assert.All(history, h => Assert.Equal(1L, h.Version));
        Assert.All(history, h => Assert.Equal(typeof(Order).FullName, h.EntityType));

        foreach (var id in ids)
            Assert.Equal(id, (await read.HistoryAsync<Order>(id, Ct)).Single().Entity!.Id);
    }

    [Fact]
    public async Task CompositeKeys_AreStoredAsAJsonArray()
    {
        await using (var db = this.Create())
        {
            db.Enrollments.AddRange(
                new Enrollment { StudentId = 1, Course = "MATH", Grade = "B" },
                new Enrollment { StudentId = 1, Course = "ART", Grade = "A" });
            await db.SaveChangesAsync(Ct);
        }
        this.Clock.Advance(TimeSpan.FromSeconds(1));
        await this.Mutate<Enrollment>(new object[] { 1, "MATH" }, e => e.Grade = "A");

        await using var read = this.Create();
        var math = await read.HistoryAsync<Enrollment>(new object[] { 1, "MATH" }, Ct);
        Assert.Equal(["B", "A"], math.Select(v => v.Entity!.Grade));
        Assert.All(math, v => Assert.Equal("[\"1\",\"MATH\"]", v.Key));
        Assert.Single(await read.HistoryAsync<Enrollment>(new object[] { 1, "ART" }, Ct));

        await Assert.ThrowsAsync<ArgumentException>(() => read.HistoryAsync<Enrollment>(1, Ct));
    }

    // ── Owned and complex ────────────────────────────────────────────────

    [Fact]
    public async Task OwnedAndComplexChanges_AreUpdatesOfTheOwner()
    {
        var order = await this.AddOrder();
        await this.Mutate<Order>(order.Id, o => o.ShippingAddress!.City = "Shelbyville");      // owned (table split)
        await this.Mutate<Order>(order.Id, o => o.Lines.Add(new OrderLine { Product = "Gadget", Qty = 3 })); // owned collection
        await this.Mutate<Order>(order.Id, o => o.Lines[0].Qty = 5);
        await this.Mutate<Order>(order.Id, o => o.Price.Amount = 20m);                         // complex
        await this.Mutate<Order>(order.Id, o => o.ShippingAddress = null);                     // owned deleted

        await using var db = this.Create();
        var history = await db.HistoryAsync<Order>(order.Id, Ct);
        Assert.Equal(6, history.Count);
        Assert.Equal(TemporalOperation.Inserted, history[0].Operation);
        Assert.All(history.Skip(1), v => Assert.Equal(TemporalOperation.Updated, v.Operation));

        Assert.Equal("Springfield", history[0].Entity!.ShippingAddress!.City);
        Assert.Equal("Shelbyville", history[1].Entity!.ShippingAddress!.City);
        Assert.Equal(2, history[2].Entity!.Lines.Count);
        Assert.Contains(history[3].Entity!.Lines, l => l.Qty == 5);
        Assert.Equal(10m, history[3].Entity!.Price.Amount);
        Assert.Equal(20m, history[4].Entity!.Price.Amount);
        Assert.Null(history[5].Entity!.ShippingAddress);
        Assert.Equal(2, history[5].Entity!.Lines.Count);

        // Snapshots hold the owned values' state, not their plumbing (owner FK, synthesized keys).
        var raw = (await this.AllHistory()).Single(h => h.Version == 1).Snapshot!;
        using var snapshot = JsonDocument.Parse(raw);
        var line = snapshot.RootElement.GetProperty("Lines")[0];
        Assert.Equal(["Product", "Qty"], line.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(["City", "Street"], snapshot.RootElement.GetProperty("ShippingAddress").EnumerateObject().Select(p => p.Name).Order());
    }

    [Fact]
    public async Task Inheritance_SharesTheBaseTypesHistory()
    {
        int dogId, catId;
        await using (var db = this.Create())
        {
            var dog = new Dog { Name = "Rex", Breed = "Collie" };
            var cat = new Cat { Name = "Tom", Lives = 9 };
            db.Animals.AddRange(dog, cat);
            await db.SaveChangesAsync(Ct);
            dogId = dog.Id;
            catId = cat.Id;
        }
        this.Clock.Advance(TimeSpan.FromSeconds(1));
        await this.Mutate<Animal>(dogId, a => ((Dog)a).Breed = "Husky");

        Assert.All(await this.AllHistory(), h => Assert.Equal(typeof(Animal).FullName, h.EntityType));

        await using var read = this.Create();
        var dogHistory = await read.HistoryAsync<Animal>(dogId, Ct);
        Assert.Equal(["Collie", "Husky"], dogHistory.Select(v => ((Dog)v.Entity!).Breed));
        Assert.Equal(["Collie", "Husky"], (await read.HistoryAsync<Dog>(dogId, Ct)).Select(v => v.Entity!.Breed));
        Assert.Empty(await read.HistoryAsync<Cat>(dogId, Ct));

        var cats = await read.AsOfAllAsync<Cat>(this.Clock.GetUtcNow(), Ct);
        Assert.Equal([catId], cats.Select(c => c.Id));
        Assert.Equal(9, cats[0].Lives);
        Assert.Equal(2, (await read.AsOfAllAsync<Animal>(this.Clock.GetUtcNow(), Ct)).Count);
    }

    // ── Atomicity ────────────────────────────────────────────────────────

    [Fact]
    public async Task RolledBackUserTransaction_LeavesNoHistory()
    {
        await using (var db = this.Create())
        {
            await using var tx = await db.Database.BeginTransactionAsync(Ct);
            db.Products.Add(new Product { Sku = "A", Name = "a" });
            await db.SaveChangesAsync(Ct);
            db.Products.Add(new Product { Sku = "B", Name = "b" });
            await db.SaveChangesAsync(Ct);
            Assert.Equal(2, await db.Set<EntityHistoryRecord>().CountAsync(Ct));   // visible inside the transaction
            await tx.RollbackAsync(Ct);
        }

        Assert.Empty(await this.AllHistory());

        await using (var db = this.Create())
        {
            await using var tx = await db.Database.BeginTransactionAsync(Ct);
            db.Products.Add(new Product { Sku = "C", Name = "c" });
            await db.SaveChangesAsync(Ct);
            await tx.CommitAsync(Ct);
        }

        Assert.Equal(["C"], (await this.AllHistory()).Select(h => h.EntityKey));
    }

    [Fact]
    public async Task AmbientTransactionScope_HistoryCommitsAndRollsBackWithIt()
    {
        if (fixture.Name == "SQLite")
            Assert.Skip("Microsoft.Data.Sqlite doesn't enlist in ambient System.Transactions.");

        using (new System.Transactions.TransactionScope(System.Transactions.TransactionScopeAsyncFlowOption.Enabled))
        {
            await using var db = this.Create();
            db.Products.Add(new Product { Sku = "A", Name = "a" });
            await db.SaveChangesAsync(Ct);
            // not completed: rolls back
        }
        Assert.Empty(await this.AllHistory());

        using (var scope = new System.Transactions.TransactionScope(System.Transactions.TransactionScopeAsyncFlowOption.Enabled))
        {
            await using (var db = this.Create())
            {
                db.Products.Add(new Product { Sku = "B", Name = "b" });
                await db.SaveChangesAsync(Ct);
            }
            scope.Complete();
        }
        Assert.Equal(["B"], (await this.AllHistory()).Select(h => h.EntityKey));
    }

    [Fact]
    public async Task FailedSave_LeavesNoHistory_AndRestoresTheTransactionBehavior()
    {
        await this.AddProduct("A", "a");

        await using (var db = this.Create())
        {
            db.Products.Add(new Product { Sku = "B", Name = "b" });
            db.Products.Add(new Product { Sku = "A", Name = "duplicate" });
            await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
            Assert.Equal(AutoTransactionBehavior.WhenNeeded, db.Database.AutoTransactionBehavior);
        }

        // A failure while writing history rolls the entity write back too.
        await using (var db = this.Create(h => h.CaptureActor = () => new string('x', 300)))
        {
            db.Products.Add(new Product { Sku = "C", Name = "c" });
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
        }

        await using var read = this.Create();
        Assert.Equal(["A"], await read.Products.Select(p => p.Sku).ToListAsync(Ct));
        Assert.Equal(["A"], (await this.AllHistory()).Select(h => h.EntityKey));
    }

    [Fact]
    public async Task RetryingExecutionStrategy_RetriedCommitWritesHistoryOnce()
    {
        await using (var db = this.Create(retrying: true, interceptors: new FailFirstCommitInterceptor()))
        {
            var order = new Order { Customer = "retry" };
            db.Orders.Add(order);
            await db.SaveChangesAsync(Ct);
            Assert.True(order.Id > 0);
        }

        await using (var db = this.Create(retrying: true))
        {
            var order = await db.Orders.SingleAsync(Ct);
            order.Status = "Paid";
            await db.SaveChangesAsync(Ct);
        }

        var history = await this.AllHistory();
        Assert.Equal([1L, 2L], history.Select(h => h.Version).Order());
        Assert.Single(history.Select(h => h.EntityKey).Distinct());
    }

    [Fact]
    public async Task ConcurrentWrites_KeepAConsistentVersionChain()
    {
        await this.AddProduct("A", "start");

        const int writers = 8;
        await Task.WhenAll(Enumerable.Range(0, writers).Select(i => Task.Run(async () =>
        {
            await using var db = this.Create();
            var product = await db.Products.SingleAsync(p => p.Sku == "A", Ct);
            product.Name = "w" + i;
            await db.SaveChangesAsync(Ct);
        }, Ct)));

        await Task.WhenAll(Enumerable.Range(0, writers).Select(i => Task.Run(async () =>
        {
            await using var db = this.Create();
            db.Products.Add(new Product { Sku = "N" + i, Name = "n" });
            await db.SaveChangesAsync(Ct);
        }, Ct)));

        await using var read = this.Create();
        var history = await read.HistoryAsync<Product>("A", Ct);
        Assert.Equal(Enumerable.Range(1, writers + 1).Select(i => (long)i), history.Select(v => v.Version));
        Assert.Single(history, v => v.ValidTo == null);
        Assert.Equal((await read.Products.SingleAsync(p => p.Sku == "A", Ct)).Name, history[^1].Entity!.Name);
        Assert.Equal(writers, (await read.AsOfAllAsync<Product>(this.Clock.GetUtcNow(), Ct)).Count(p => p.Sku.StartsWith('N')));
    }

    // ── Bulk writes ──────────────────────────────────────────────────────

    [Fact]
    public async Task BulkWrites_AgainstHistoryEntities_Throw_UnlessAllowed()
    {
        await this.AddProduct("A", "a");
        await this.AddOrder();

        await using (var allowed = this.Create(h => h.AllowBulkWrites = true))
            Assert.Equal(1, await allowed.Products.Where(p => p.Sku == "A").ExecuteUpdateAsync(s => s.SetProperty(p => p.Name, "bulk"), Ct));

        await using (var db = this.Create())
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                db.Products.Where(p => p.Sku == "A").ExecuteUpdateAsync(s => s.SetProperty(p => p.Name, "bulk"), Ct));
            Assert.Contains("AllowBulkWrites", ex.Message);
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.Orders.ExecuteDeleteAsync(Ct));
            Assert.Throws<InvalidOperationException>(() => db.Animals.OfType<Dog>().ExecuteDelete());

            // Untracked types, and history types used only in a filter, are fine.
            db.Notes.Add(new Note { Text = "n" });
            await db.SaveChangesAsync(Ct);
            Assert.Equal(1, await db.Notes.Where(n => db.Products.Any(p => p.Sku == "A")).ExecuteDeleteAsync(Ct));
        }

        await using (var allowed = this.Create(h => h.AllowBulkWrites = true))
            Assert.Equal(1, await allowed.Products.ExecuteDeleteAsync(Ct));

        // The allowed writes left no versions behind - that's the documented trade-off.
        Assert.Equal(["Inserted"], (await this.AllHistory()).Where(h => h.EntityKey == "A").Select(h => h.Operation.ToString()));
    }
}
