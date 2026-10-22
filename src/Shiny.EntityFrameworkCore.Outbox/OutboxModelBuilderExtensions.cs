using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Shiny.EntityFrameworkCore.Outbox;

/// <summary>Maps <see cref="OutboxMessage"/> into your model, so your migrations create the outbox table.</summary>
public static class OutboxModelBuilderExtensions
{
    /// <summary>The table outbox messages live in unless <see cref="AddOutbox"/> is told otherwise.</summary>
    public const string DefaultTableName = "outbox";

    /// <summary>
    /// Maps <see cref="OutboxMessage"/> as an entity of this model: its own table (so infrastructure rows never mix
    /// into a business table, and the processor's hot poll never touches one), explicitly pinned column names,
    /// <see cref="OutboxMessage.Version"/> as a concurrency token, and the index the poll reads through.
    /// <code>
    /// protected override void OnModelCreating(ModelBuilder modelBuilder)
    ///     => modelBuilder.AddOutbox();
    /// </code>
    /// Then <c>dotnet ef migrations add AddOutbox</c> picks the table up like any other entity.
    /// </summary>
    /// <param name="modelBuilder">The model being built.</param>
    /// <param name="tableName">The table to store messages in.</param>
    /// <param name="schema">The schema, for providers that have them. Null uses the model's default schema.</param>
    public static ModelBuilder AddOutbox(this ModelBuilder modelBuilder, string tableName = DefaultTableName, string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        modelBuilder.Entity<OutboxMessage>(b =>
        {
            b.ToTable(tableName, schema);

            b.HasKey(x => x.Id).HasName($"pk_{tableName}");
            b.Property(x => x.Id)
                .HasColumnName(OutboxColumns.Id)
                .HasMaxLength(32)
                .IsUnicode(false)
                .ValueGeneratedNever();

            b.Property(x => x.MessageType).HasColumnName(OutboxColumns.MessageType).HasMaxLength(512).IsRequired();
            b.Property(x => x.Payload).HasColumnName(OutboxColumns.Payload).IsRequired();
            b.Property(x => x.PartitionKey).HasColumnName(OutboxColumns.PartitionKey).HasMaxLength(512);
            b.Property(x => x.Headers)
                .HasColumnName(OutboxColumns.Headers)
                .HasConversion(HeadersConverter, HeadersComparer);

            // Timestamps are stored as UTC DateTime rather than DateTimeOffset. Every value the outbox writes is
            // UTC anyway, and the DateTime mapping is the one every provider can compare and order in SQL — SQLite
            // cannot ORDER BY a DateTimeOffset at all, and the processor's poll and the admin both do.
            Timestamp(b.Property(x => x.CreatedAt).HasColumnName(OutboxColumns.CreatedAt));
            Timestamp(b.Property(x => x.AvailableAt).HasColumnName(OutboxColumns.AvailableAt));
            Timestamp(b.Property(x => x.ProcessedAt).HasColumnName(OutboxColumns.ProcessedAt));
            Timestamp(b.Property(x => x.DeadLetteredAt).HasColumnName(OutboxColumns.DeadLetteredAt));

            b.Property(x => x.Attempts).HasColumnName(OutboxColumns.Attempts);
            b.Property(x => x.Error).HasColumnName(OutboxColumns.Error);

            // The claim primitive: every processor write is conditioned on the version it read and bumps it, so two
            // processors racing for one message cannot both win. No lease table, no leader election.
            b.Property(x => x.Version).HasColumnName(OutboxColumns.Version).IsConcurrencyToken();

            // The hot poll: undelivered (both nulls) and past the backoff gate.
            b.HasIndex(x => new { x.ProcessedAt, x.DeadLetteredAt, x.AvailableAt }).HasDatabaseName($"ix_{tableName}_poll");
        });
        return modelBuilder;
    }

    static void Timestamp<TProperty>(Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<TProperty> property)
        => property.HasConversion(UtcConverter).HasPrecision(6);

    static readonly ValueConverter<DateTimeOffset, DateTime> UtcConverter = new(
        v => v.UtcDateTime,
        v => new DateTimeOffset(DateTime.SpecifyKind(v, DateTimeKind.Utc)));

    static readonly ValueConverter<Dictionary<string, string>?, string> HeadersConverter = new(
        v => JsonSerializer.Serialize(v!, OutboxJsonContext.Default.DictionaryStringString),
        v => JsonSerializer.Deserialize(v, OutboxJsonContext.Default.DictionaryStringString)!);

    static readonly ValueComparer<Dictionary<string, string>?> HeadersComparer = new(
        (a, b) => HeadersEqual(a, b),
        v => HeadersHash(v),
        v => HeadersSnapshot(v));

    static bool HeadersEqual(Dictionary<string, string>? a, Dictionary<string, string>? b)
    {
        if (ReferenceEquals(a, b))
            return true;
        if (a == null || b == null || a.Count != b.Count)
            return false;
        return a.All(pair => b.TryGetValue(pair.Key, out var value) && value == pair.Value);
    }

    static int HeadersHash(Dictionary<string, string>? v)
        => v == null ? 0 : v.Aggregate(v.Count, (hash, pair) => hash ^ HashCode.Combine(pair.Key, pair.Value));

    static Dictionary<string, string>? HeadersSnapshot(Dictionary<string, string>? v)
        => v == null ? null : new Dictionary<string, string>(v, StringComparer.Ordinal);
}
