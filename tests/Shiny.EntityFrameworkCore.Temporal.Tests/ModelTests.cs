using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Shiny.EntityFrameworkCore.Temporal.Tests;

/// <summary>A context whose model is built by the test; never cached, so each test gets its own model.</summary>
public sealed class ConfigurableContext(DbContextOptions<ConfigurableContext> options, Action<ModelBuilder> configure) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) => configure(modelBuilder);

    public static ConfigurableContext Create(Action<ModelBuilder> configure, string connection = "Data Source=:memory:")
        => new(new DbContextOptionsBuilder<ConfigurableContext>()
            .UseSqlite(connection)
            .UseEntityHistory()
            .ReplaceService<IModelCacheKeyFactory, UncachedModelKeyFactory>()
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .Options, configure);

    sealed class UncachedModelKeyFactory : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime) => new object();
    }
}

public readonly record struct TicketId(int Value);

public class Ticket
{
    public TicketId Id { get; set; }
    public Guid Ref { get; set; }
}

public class KeylessEntity
{
    public string Name { get; set; } = "";
}

public class ModelTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void HasHistory_AddsTheSharedHistoryTable()
    {
        using var db = ConfigurableContext.Create(m => m.Entity<Product>(b =>
        {
            b.HasKey(x => x.Sku);
            b.HasHistory(h =>
            {
                h.Retention = TimeSpan.FromDays(30);
                h.MaxVersions = 10;
            });
        }));

        var history = db.Model.FindEntityType(typeof(EntityHistoryRecord))!;
        Assert.Equal("EntityHistory", history.GetTableName());
        Assert.Equal(
            [nameof(EntityHistoryRecord.EntityType), nameof(EntityHistoryRecord.EntityKey), nameof(EntityHistoryRecord.Version)],
            history.FindPrimaryKey()!.Properties.Select(p => p.Name));
        Assert.Equal(128, history.FindProperty(nameof(EntityHistoryRecord.EntityType))!.GetMaxLength());
        Assert.Equal(256, history.FindProperty(nameof(EntityHistoryRecord.EntityKey))!.GetMaxLength());
        Assert.Equal(256, history.FindProperty(nameof(EntityHistoryRecord.Actor))!.GetMaxLength());
        Assert.Equal(6, history.FindProperty(nameof(EntityHistoryRecord.ValidFrom))!.GetPrecision());
        Assert.Equal(typeof(string), history.FindProperty(nameof(EntityHistoryRecord.Operation))!.GetTypeMapping().Converter!.ProviderClrType);
        Assert.Contains(history.GetIndexes(), i => i.Properties.Select(p => p.Name).SequenceEqual([nameof(EntityHistoryRecord.EntityType), nameof(EntityHistoryRecord.ValidFrom)]));
        Assert.Contains(history.GetIndexes(), i => i.Properties.Select(p => p.Name).SequenceEqual([nameof(EntityHistoryRecord.EntityType), nameof(EntityHistoryRecord.Actor)]));

        var product = db.Model.FindEntityType(typeof(Product))!;
        Assert.Equal(typeof(Product).FullName, product.FindAnnotation("Shiny:History:Name")!.Value);
        Assert.Equal(TimeSpan.FromDays(30), product.FindAnnotation("Shiny:History:Retention")!.Value);
        Assert.Equal(10, product.FindAnnotation("Shiny:History:MaxVersions")!.Value);
    }

    [Fact]
    public async Task ExplicitHistoryTableConfiguration_Wins_AndIsUsedForWrites()
    {
        var file = Path.Combine(Path.GetTempPath(), "temporal-model-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            void Configure(ModelBuilder m)
            {
                m.Entity<EntityHistoryRecord>().ToTable("Audit", t => t.HasComment("custom"));
                m.Entity<EntityHistoryRecord>().Property(x => x.Snapshot).HasColumnName("Body");
                m.Entity<Note>().HasHistory(h => h.Name = "note");
            }

            await using (var db = ConfigurableContext.Create(Configure, $"Data Source={file};Pooling=False"))
            {
                Assert.Equal("Audit", db.Model.FindEntityType(typeof(EntityHistoryRecord))!.GetTableName());
                await db.Database.EnsureCreatedAsync(Ct);
                db.Add(new Note { Text = "hello" });
                await db.SaveChangesAsync(Ct);
            }

            await using (var db = ConfigurableContext.Create(Configure, $"Data Source={file};Pooling=False"))
            {
                var history = await db.HistoryAsync<Note>(1, Ct);
                Assert.Equal("hello", history.Single().Entity!.Text);
                Assert.Equal("note", (await db.Set<EntityHistoryRecord>().SingleAsync(Ct)).EntityType);
            }
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void HasHistory_OnAKeylessType_Throws()
    {
        using var db = ConfigurableContext.Create(m => m.Entity<KeylessEntity>().HasNoKey().HasHistory());
        Assert.Contains("keyless", Assert.Throws<InvalidOperationException>(() => db.Model).Message);
    }

    [Fact]
    public void HasHistory_OnADerivedTypeOfAMappedBase_Throws()
    {
        using var db = ConfigurableContext.Create(m =>
        {
            m.Entity<Animal>().HasHistory();
            m.Entity<Dog>().HasHistory();
        });
        Assert.Contains("base type", Assert.Throws<InvalidOperationException>(() => db.Model).Message);
    }

    [Fact]
    public void HasHistory_OnTheHistoryTable_Throws()
    {
        using var db = ConfigurableContext.Create(m => m.Entity<EntityHistoryRecord>().HasHistory());
        Assert.Throws<InvalidOperationException>(() => db.Model);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("blank")]
    [InlineData("retention")]
    [InlineData("max")]
    public void HasHistory_RejectsInvalidOptions(string invalid)
    {
        using var db = ConfigurableContext.Create(m => m.Entity<Note>().HasHistory(h =>
        {
            switch (invalid)
            {
                case "name": h.Name = new string('n', 129); break;
                case "blank": h.Name = " "; break;
                case "retention": h.Retention = TimeSpan.Zero; break;
                default: h.MaxVersions = 0; break;
            }
        }));
        Assert.ThrowsAny<ArgumentException>(() => db.Model);
    }

    [Fact]
    public void HasHistory_CalledAgain_ReplacesTheSettings()
    {
        using var db = ConfigurableContext.Create(m =>
        {
            m.Entity<Note>().HasHistory(h => h.MaxVersions = 3);
            m.Entity<Note>().HasHistory(h => h.Name = "notes");
        });
        var note = db.Model.FindEntityType(typeof(Note))!;
        Assert.Equal("notes", note.FindAnnotation("Shiny:History:Name")!.Value);
        Assert.Null(note.FindAnnotation("Shiny:History:MaxVersions"));
    }

    [Fact]
    public void UseEntityHistory_CalledTwice_KeepsOneInterceptorAndMergesSettings()
    {
        var clock = new ManualClock();
        var options = new DbContextOptionsBuilder<ConfigurableContext>()
            .UseSqlite("Data Source=:memory:")
            .UseEntityHistory(o => o.TimeProvider = clock)
            .UseEntityHistory(o => o.CaptureActor = () => "me")
            .Options;

        var core = options.FindExtension<CoreOptionsExtension>()!;
        Assert.Single(core.Interceptors!, i => i.GetType().Name == "HistoryInterceptor");
        Assert.Single(core.Interceptors!, i => i.GetType().Name == "BulkWriteGuardInterceptor");

        using var db = new ConfigurableContext(options, _ => { });
        var settings = Internal.TemporalHistoryOptionsExtension.Find(db)!;
        Assert.Same(clock, settings.TimeProvider);
        Assert.Equal("me", settings.CaptureActor!());
    }

    [Fact]
    public void Keys_AreFormattedFromTheProviderValue()
    {
        using var db = ConfigurableContext.Create(m =>
        {
            m.Entity<Ticket>(b =>
            {
                b.Property(x => x.Id).HasConversion(v => v.Value, v => new TicketId(v));
                b.HasHistory();
            });
            m.Entity<Enrollment>(b =>
            {
                b.HasKey(x => new { x.StudentId, x.Course });
                b.HasHistory();
            });
        });

        var ticket = db.Model.FindEntityType(typeof(Ticket))!;
        Assert.Equal("5", Internal.KeyFormatter.FromUserKey(ticket, new TicketId(5)));
        Assert.Equal("5", Internal.KeyFormatter.FromUserKey(ticket, 5));

        var enrollment = db.Model.FindEntityType(typeof(Enrollment))!;
        Assert.Equal("[\"7\",\"a/b\"]", Internal.KeyFormatter.FromUserKey(enrollment, new object[] { 7, "a/b" }));
        Assert.Throws<ArgumentException>(() => Internal.KeyFormatter.FromUserKey(enrollment, new object?[] { 7, null }));

        var entry = db.Entry(new Ticket { Id = new TicketId(9), Ref = Guid.Empty });
        Assert.Equal("9", Internal.KeyFormatter.FromEntry(entry, original: false));
    }

    [Fact]
    public void Migrations_ScaffoldTheHistoryTableAndAnnotations()
    {
        using var db = ConfigurableContext.Create(m => m.Entity<Product>(b =>
        {
            b.HasKey(x => x.Sku);
            b.HasHistory(h =>
            {
                h.Retention = TimeSpan.FromDays(7);
                h.MaxVersions = 5;
            });
        }));

        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection()
            .AddEntityFrameworkDesignTimeServices()
            .AddDbContextDesignTimeServices(db);
        new Microsoft.EntityFrameworkCore.Sqlite.Design.Internal.SqliteDesignTimeServices().ConfigureDesignTimeServices(services);
        using var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);

        var scaffolder = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
            .GetRequiredService<Microsoft.EntityFrameworkCore.Migrations.Design.IMigrationsScaffolder>(provider);
        var migration = scaffolder.ScaffoldMigration("AddHistory", "Tests");

        Assert.Contains("name: \"EntityHistory\"", migration.MigrationCode);
        Assert.Contains("IX_EntityHistory_EntityType_Actor", migration.MigrationCode);
        Assert.Contains("Shiny:History:Retention", migration.SnapshotCode);
    }

    [Fact]
    public async Task ReadApis_RejectUnknownTypes()
    {
        await using var db = ConfigurableContext.Create(m => m.Entity<Note>().HasHistory());
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.HistoryAsync<Product>("x", Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => db.HistoryAsync<Note>(null!, Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => db.ChangesByActorAsync<Note>(null!, Ct));
    }
}
