using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace Shiny.EntityFrameworkCore.AI.Implementation;

/// <summary>
/// Reads the loosely-typed argument bag an <see cref="AIFunction"/> is invoked with. Values arrive as
/// <see cref="JsonElement"/> from MCP and most chat clients, but a hand-rolled caller may pass CLR values, a
/// <see cref="JsonNode"/> or a JSON string — all are accepted.
/// </summary>
static class AIArguments
{
    /// <summary>Reads a string argument (a JSON number is accepted and stringified — ids often arrive as numbers).</summary>
    public static string? GetString(AIFunctionArguments arguments, string key)
    {
        if (!arguments.TryGetValue(key, out var raw) || raw is null)
            return null;

        return raw switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
            JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
            JsonElement e => e.GetRawText(),
            _ => Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture)
        };
    }

    /// <summary>Reads an integer argument, or returns <paramref name="fallback"/> when absent, null or not an integer.</summary>
    public static int GetInt(AIFunctionArguments arguments, string key, int fallback)
    {
        if (!arguments.TryGetValue(key, out var raw) || raw is null)
            return fallback;

        return raw switch
        {
            int i => i,
            long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
            JsonElement { ValueKind: JsonValueKind.Number } e when e.TryGetInt32(out var i) => i,
            JsonElement { ValueKind: JsonValueKind.String } e when int.TryParse(e.GetString(), out var i) => i,
            string s when int.TryParse(s, out var i) => i,
            _ => fallback
        };
    }

    /// <summary>Reads an argument that is itself JSON (the structured filter, an entity body).</summary>
    public static JsonElement? GetJson(AIFunctionArguments arguments, string key)
    {
        if (!arguments.TryGetValue(key, out var raw) || raw is null)
            return null;

        switch (raw)
        {
            case JsonElement element:
                return element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : element;

            case JsonNode node:
                return Parse(node.ToJsonString());

            case string text when text.Length > 0:
                return Parse(text);

            default:
                throw new EntityAIToolException($"'{key}' must be a JSON object.");
        }
    }

    static JsonElement Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new EntityAIToolException("An argument expected to be JSON could not be parsed.");
        }
    }
}
