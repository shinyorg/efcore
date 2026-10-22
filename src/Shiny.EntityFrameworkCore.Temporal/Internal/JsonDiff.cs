using System.Text.Json;
using System.Text.Json.Nodes;

namespace Shiny.EntityFrameworkCore.Temporal.Internal;

/// <summary>
/// Builds an RFC 6902 patch between two snapshots. Objects (owned and complex values) are diffed member by
/// member; arrays and scalars are replaced whole when they differ.
/// </summary>
static class JsonDiff
{
    public static JsonPatchDocument<T> CreatePatch<T>(string originalJson, string modifiedJson, JsonSerializerOptions options) where T : class
    {
        var original = JsonNode.Parse(originalJson)?.AsObject()
            ?? throw new InvalidOperationException("Original snapshot is not a JSON object.");
        var modified = JsonNode.Parse(modifiedJson)?.AsObject()
            ?? throw new InvalidOperationException("Modified snapshot is not a JSON object.");

        var operations = new List<JsonPatchOperation>();
        BuildDiff(original, modified, "", operations);
        return new JsonPatchDocument<T>(operations, options);
    }

    static void BuildDiff(JsonObject original, JsonObject modified, string prefix, List<JsonPatchOperation> operations)
    {
        foreach (var prop in modified)
        {
            var path = prefix + "/" + EscapePointer(prop.Key);
            var hadKey = original.ContainsKey(prop.Key);
            var origValue = original[prop.Key];

            if (!hadKey || (origValue is null && prop.Value is not null))
            {
                // JsonObject stores JSON null as a C# null, so "key absent" is told apart with ContainsKey.
                operations.Add(JsonPatchOperation.Add(path, ToJsonElement(prop.Value)));
            }
            else if (prop.Value is null && origValue is not null)
            {
                operations.Add(JsonPatchOperation.Replace(path, ToJsonElement(null)));
            }
            else if (origValue is JsonObject origObj && prop.Value is JsonObject modObj)
            {
                BuildDiff(origObj, modObj, path, operations);
            }
            else if (origValue is not null && !JsonNode.DeepEquals(origValue, prop.Value))
            {
                operations.Add(JsonPatchOperation.Replace(path, ToJsonElement(prop.Value)));
            }
        }

        foreach (var prop in original)
        {
            if (!modified.ContainsKey(prop.Key))
                operations.Add(JsonPatchOperation.Remove(prefix + "/" + EscapePointer(prop.Key)));
        }
    }

    // RFC 6901: '~' -> '~0' then '/' -> '~1'.
    static string EscapePointer(string key) => key.Replace("~", "~0").Replace("/", "~1");

    static JsonElement ToJsonElement(JsonNode? node)
        => node is null
            ? JsonDocument.Parse("null").RootElement.Clone()
            : JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();
}
