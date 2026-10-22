# Shiny EF Core Extensions

Feature packages for **EF Core 10** that fill the gaps EF leaves:
- **Spatial, vector and full-text search on SQLite**, including iOS and Android, where `mod_spatialite` can't run.
- **A transactional outbox.**
- **Temporal history on every relational provider**, not only SQL Server.
- **Scoped AI tools and an MCP server** over a `DbContext`.
- **Versioned seeding**, plus an **Aspire seed gate**.

Every package plugs in through EF's own extension points, so you never derive from a base `DbContext`.

Docs: https://shinylib.net/efcore

| Package | What it does |
|---|---|
| `Shiny.EntityFrameworkCore.Sqlite.Mobile` | NetTopologySuite spatial predicates without SpatiaLite (with an R*Tree index), sqlite-vec vectors, FTS5 full-text |
| `Shiny.EntityFrameworkCore.Outbox` | Enqueue messages in your `SaveChanges` transaction; background processor with retries, dead letters, ordered partitions |
| `Shiny.EntityFrameworkCore.Temporal` | `HasHistory()` → History / AsOf / AsOfAll / Restore / JSON Patch diffs / ChangesByActor |
| `Shiny.EntityFrameworkCore.AI` | Microsoft.Extensions.AI tools whose per-entity scope the model cannot widen |
| `Shiny.EntityFrameworkCore.Mcp` | The same scoped tools as an MCP server |
| `Shiny.EntityFrameworkCore.Seeding` | Run-once seeders keyed by name + version, exactly once across instances |
| `Shiny.EntityFrameworkCore.Aspire.Hosting` | AppHost seed gate: seed on first setup or on a deliberate recreate |

The outbox, temporal history, AI tools and seeding are tested on SQLite, PostgreSQL, SQL Server and MySQL
(`MySql.EntityFrameworkCore`). `Sqlite.Mobile` is SQLite-only.

## SQLite on mobile

```csharp
services.AddDbContext<AppDbContext>(o => o.UseSqlite(cs, s => s
    .UseShinySpatial()      // NTS predicates + GeoDistanceMeters, no mod_spatialite
    .UseShinyVectors()      // bundled sqlite-vec (static on iOS, 16 KB-aligned on Android)
    .UseShinyFullText()));  // FTS5

// OnModelCreating
e.HasSpatialIndex(x => x.Location);
e.HasVectorIndex(x => x.Embedding, dimensions: 384);
e.HasFullTextIndex(x => new { x.Title, x.Body });

// queries
db.Places.Where(p => p.Location!.Within(area));
db.Places.Where(p => EF.Functions.IsWithinMeters(p.Location, here, 500));
db.Docs.NearestTo(d => d.Embedding, query, k: 5);
db.Docs.Where(d => EF.Functions.FullTextMatch(d, "tomato"))
       .OrderBy(d => EF.Functions.FullTextRank(d, "tomato"));
```

## Outbox

```csharp
protected override void OnModelCreating(ModelBuilder mb) => mb.AddOutbox();

services.AddDbContext<AppDbContext>(o => o.UseNpgsql(cs)
    .AddOutboxPublishing(p => p.Publish<Order>(c => new OrderChanged(c.Entity.Id, c.Operation))));
services.AddDbContextOutbox<AppDbContext, BusDispatcher>();

db.Orders.Add(order);
db.Enqueue(new OrderPlaced(order.Id));   // same transaction as the order
await db.SaveChangesAsync();
```

## Temporal history

```csharp
modelBuilder.Entity<Order>().HasHistory(h => h.MaxVersions = 50);
services.AddDbContext<AppDbContext>(o => o.UseNpgsql(cs)
    .UseEntityHistory(h => h.ResolveActor = sp => sp.GetService<ICurrentUser>()?.Name));

var versions = await db.HistoryAsync<Order>(id);
var lastWeek = await db.AsOfAsync<Order>(id, DateTimeOffset.UtcNow.AddDays(-7));
var patch    = await db.GetDiffBetweenAsync<Order>(id, 1, 3);
await db.RestoreAsync<Order>(id, version: 2);
```

## AI tools & MCP

```csharp
services.AddDbContextAITools<AppDbContext>(b => b.AddEntity<Order>(t => t
    .Where<ICurrentTenant>((tenant, _) => o => o.TenantId == tenant.Id)   // the model cannot remove or widen this scope
    .AllowQuery().AllowCount().AllowAggregate().AllowGet()
    .Ignore(o => o.InternalNotes)));

var tools = sp.GetRequiredService<DbContextAITools<AppDbContext>>().Tools;

services.AddMcpServer().WithHttpTransport()
    .WithDbContext<AppDbContext>(m => m.ExposeResources().AddEntity<Order>(/* same scope */));
```

## Versioned seeding & Aspire

```csharp
// app
services.AddDbContext<AppDbContext>(o => o.UseNpgsql(cs).UseVersionedSeeding<AppDbContext>());
services.AddDbSeeder<AppDbContext, CountrySeeder>();   // runs again only when CountrySeeder.Version goes up

// AppHost
builder.AddPostgres("pg").WithDataVolume().AddDatabase("appdb")
    .WithDbContextSeeder<AppDbContext>(
        (o, cs) => o.UseNpgsql(cs),
        (db, ctx, ct) => DbSeedRunner.RunAsync(db, [new CountrySeeder()], ct),
        DbSeedMode.FirstTimeOnly);
```
