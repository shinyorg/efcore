using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Shiny.EntityFrameworkCore.AI.Implementation;

/// <summary>
/// The closed set of CLR types a field may have, and the conversions between them and JSON. Anything outside it is
/// not exposed at all — a field the structured filter cannot express is a field the model would only misuse.
/// </summary>
static class ScalarTypes
{
    public static bool IsSupported(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        return t.IsEnum
            || t == typeof(string) || t == typeof(bool) || t == typeof(char) || t == typeof(Guid)
            || t == typeof(DateTime) || t == typeof(DateTimeOffset) || t == typeof(DateOnly) || t == typeof(TimeOnly) || t == typeof(TimeSpan)
            || IsNumeric(t);
    }

    public static bool IsNumeric(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        return t == typeof(byte) || t == typeof(sbyte) || t == typeof(short) || t == typeof(ushort)
            || t == typeof(int) || t == typeof(uint) || t == typeof(long) || t == typeof(ulong)
            || t == typeof(float) || t == typeof(double) || t == typeof(decimal);
    }

    /// <summary>Types min/max make sense for: numbers plus the date/time family.</summary>
    public static bool IsOrderable(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        return IsNumeric(t)
            || t == typeof(DateTime) || t == typeof(DateTimeOffset) || t == typeof(DateOnly) || t == typeof(TimeOnly) || t == typeof(TimeSpan);
    }

    /// <summary>JSON-schema type, format and enum values for a CLR type.</summary>
    public static (string Type, string? Format, string[]? Enum) Classify(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        if (t.IsEnum) return ("string", null, Enum.GetNames(t));
        if (t == typeof(bool)) return ("boolean", null, null);
        if (t == typeof(Guid)) return ("string", "uuid", null);
        if (t == typeof(DateTime) || t == typeof(DateTimeOffset)) return ("string", "date-time", null);
        if (t == typeof(DateOnly)) return ("string", "date", null);
        if (t == typeof(TimeOnly)) return ("string", "time", null);
        if (t == typeof(TimeSpan)) return ("string", "duration", null);
        if (t == typeof(float) || t == typeof(double) || t == typeof(decimal)) return ("number", null, null);
        if (IsNumeric(t)) return ("integer", null, null);
        return ("string", null, null);
    }

    /// <summary>
    /// Converts a model-supplied JSON value to <paramref name="targetType"/>. Every failure is a clean, field-named
    /// tool error rather than a parser exception.
    /// </summary>
    public static object? FromJson(JsonElement value, Type targetType, string fieldName)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            if (targetType.IsValueType && Nullable.GetUnderlyingType(targetType) == null)
                throw new EntityAIToolException($"Field '{fieldName}' is not nullable.");
            return null;
        }

        try
        {
            return Convert(value, Nullable.GetUnderlyingType(targetType) ?? targetType);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or OverflowException or ArgumentException)
        {
            throw new EntityAIToolException(
                $"Value {Truncate(value.GetRawText())} is not a valid {Describe(targetType)} for field '{fieldName}'.");
        }
    }

    /// <summary>Converts a raw string (an id argument) to <paramref name="targetType"/>.</summary>
    public static object FromString(string value, Type targetType, string fieldName)
    {
        var t = Nullable.GetUnderlyingType(targetType) ?? targetType;
        try
        {
            if (t == typeof(string))
                return value;

            using var document = JsonDocument.Parse($"\"{JsonEncodedText.Encode(value)}\"");
            return Convert(document.RootElement, t)!;
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or OverflowException or ArgumentException)
        {
            throw new EntityAIToolException($"'{Truncate(value)}' is not a valid {Describe(targetType)} for '{fieldName}'.");
        }
    }

    static object? Convert(JsonElement value, Type t)
    {
        // Numbers frequently arrive as strings from models; accept both for every non-string type.
        var text = value.ValueKind == JsonValueKind.String ? value.GetString()! : null;
        var invariant = CultureInfo.InvariantCulture;

        if (t == typeof(string)) return text ?? value.GetRawText();
        if (t.IsEnum) return ConvertEnum(value, t);
        if (t == typeof(bool)) return text != null ? bool.Parse(text) : value.GetBoolean();
        if (t == typeof(char)) return (text ?? value.GetRawText()) is { Length: 1 } c ? c[0] : throw new FormatException();
        if (t == typeof(Guid)) return Guid.Parse(text ?? throw new FormatException());
        if (t == typeof(DateTime)) return text != null ? DateTime.Parse(text, invariant, DateTimeStyles.RoundtripKind) : throw new FormatException();
        if (t == typeof(DateTimeOffset)) return text != null ? DateTimeOffset.Parse(text, invariant, DateTimeStyles.RoundtripKind) : throw new FormatException();
        if (t == typeof(DateOnly)) return DateOnly.Parse(text ?? throw new FormatException(), invariant);
        if (t == typeof(TimeOnly)) return TimeOnly.Parse(text ?? throw new FormatException(), invariant);
        if (t == typeof(TimeSpan)) return TimeSpan.Parse(text ?? throw new FormatException(), invariant);
        if (t == typeof(int)) return text != null ? int.Parse(text, invariant) : value.GetInt32();
        if (t == typeof(long)) return text != null ? long.Parse(text, invariant) : value.GetInt64();
        if (t == typeof(short)) return text != null ? short.Parse(text, invariant) : value.GetInt16();
        if (t == typeof(byte)) return text != null ? byte.Parse(text, invariant) : value.GetByte();
        if (t == typeof(sbyte)) return text != null ? sbyte.Parse(text, invariant) : value.GetSByte();
        if (t == typeof(ushort)) return text != null ? ushort.Parse(text, invariant) : value.GetUInt16();
        if (t == typeof(uint)) return text != null ? uint.Parse(text, invariant) : value.GetUInt32();
        if (t == typeof(ulong)) return text != null ? ulong.Parse(text, invariant) : value.GetUInt64();
        if (t == typeof(double)) return text != null ? double.Parse(text, invariant) : value.GetDouble();
        if (t == typeof(float)) return text != null ? float.Parse(text, invariant) : value.GetSingle();
        if (t == typeof(decimal)) return text != null ? decimal.Parse(text, invariant) : value.GetDecimal();

        throw new FormatException();
    }

    static object ConvertEnum(JsonElement value, Type enumType)
    {
        if (value.ValueKind == JsonValueKind.Number)
        {
            var number = Enum.ToObject(enumType, value.GetInt64());
            return Enum.IsDefined(enumType, number) ? number : throw new FormatException();
        }

        var name = value.GetString() ?? throw new FormatException();
        return Enum.TryParse(enumType, name, ignoreCase: true, out var parsed) && Enum.IsDefined(enumType, parsed!)
            ? parsed!
            : throw new FormatException();
    }

    /// <summary>Writes a CLR value as JSON without the reflection serializer.</summary>
    public static JsonNode? ToJson(object? value) => value switch
    {
        null => null,
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        char c => JsonValue.Create(c.ToString()),
        Enum e => JsonValue.Create(e.ToString()),
        int i => JsonValue.Create(i),
        long l => JsonValue.Create(l),
        short s => JsonValue.Create(s),
        byte b => JsonValue.Create(b),
        sbyte b => JsonValue.Create(b),
        ushort u => JsonValue.Create(u),
        uint u => JsonValue.Create(u),
        ulong u => JsonValue.Create(u),
        float f => float.IsFinite(f) ? JsonValue.Create(f) : null,
        double d => double.IsFinite(d) ? JsonValue.Create(d) : null,
        decimal m => JsonValue.Create(m),
        Guid g => JsonValue.Create(g.ToString()),
        DateTime dt => JsonValue.Create(dt.ToString("O", CultureInfo.InvariantCulture)),
        DateTimeOffset dto => JsonValue.Create(dto.ToString("O", CultureInfo.InvariantCulture)),
        DateOnly d => JsonValue.Create(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
        TimeOnly t => JsonValue.Create(t.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture)),
        TimeSpan ts => JsonValue.Create(ts.ToString("c", CultureInfo.InvariantCulture)),
        _ => JsonValue.Create(value.ToString())
    };

    static string Describe(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        return t.IsEnum ? $"one of [{string.Join(", ", Enum.GetNames(t))}]" : t.Name;
    }

    static string Truncate(string raw) => raw.Length <= 64 ? raw : raw[..64] + "…";
}
