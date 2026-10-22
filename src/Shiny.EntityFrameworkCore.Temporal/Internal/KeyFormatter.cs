using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Shiny.EntityFrameworkCore.Temporal.Internal;

/// <summary>
/// Turns a primary key into the stable string stored in <see cref="EntityHistoryRecord.EntityKey"/>. Values are
/// formatted from their <b>provider</b> value (after any value converter) with invariant culture, so a strongly
/// typed id and its raw value produce the same key. A single key is its plain text; a composite key is a JSON
/// array of the per-column texts, in key order.
/// </summary>
static class KeyFormatter
{
    public static string FromEntry(EntityEntry entry, bool original)
    {
        var key = entry.Metadata.FindPrimaryKey()
            ?? throw new InvalidOperationException($"'{entry.Metadata.DisplayName()}' has no primary key.");
        var values = new object?[key.Properties.Count];
        for (var i = 0; i < values.Length; i++)
        {
            var property = entry.Property(key.Properties[i].Name);
            values[i] = original ? property.OriginalValue : property.CurrentValue;
        }
        return Format(key, values);
    }

    /// <summary>
    /// Formats a key supplied by a caller of the read API: a single value, or an <c>object[]</c> with one value per
    /// key property (the same shape <c>DbContext.Find</c> takes).
    /// </summary>
    public static string FromUserKey(IEntityType entityType, object key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var pk = entityType.FindPrimaryKey()
            ?? throw new InvalidOperationException($"'{entityType.DisplayName()}' has no primary key.");
        var values = key is object?[] array ? array : [key];
        if (values.Length != pk.Properties.Count)
            throw new ArgumentException(
                $"'{entityType.DisplayName()}' has a {pk.Properties.Count}-part key but {values.Length} value(s) were supplied. Pass an object[] for composite keys, in key order.",
                nameof(key));
        return Format(pk, values);
    }

    /// <summary>Formats key values of <paramref name="key"/> already in key-property order.</summary>
    public static string Format(IKey key, object?[] values)
    {
        if (values.Length == 1)
            return FormatValue(key.Properties[0], values[0]);

        var parts = new string[values.Length];
        for (var i = 0; i < values.Length; i++)
            parts[i] = FormatValue(key.Properties[i], values[i]);
        return JsonSerializer.Serialize(parts);
    }

    static string FormatValue(IProperty property, object? value)
    {
        if (value == null)
            throw new ArgumentException($"Key property '{property.Name}' has no value.");

        // Callers may hand us the CLR value (a strongly typed id) or the provider value already.
        var converter = property.GetTypeMapping().Converter;
        if (converter != null && converter.ModelClrType.IsInstanceOfType(value))
            value = converter.ConvertToProvider(value);

        return value switch
        {
            null => throw new ArgumentException($"Key property '{property.Name}' converted to null."),
            string s => s,
            Guid g => g.ToString("D"),
            DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
            byte[] bytes => Convert.ToBase64String(bytes),
            bool b => b ? "true" : "false",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? throw new ArgumentException($"Key property '{property.Name}' has no text form.")
        };
    }
}
