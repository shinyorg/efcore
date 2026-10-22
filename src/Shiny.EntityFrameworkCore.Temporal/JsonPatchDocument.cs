using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Shiny.EntityFrameworkCore.Temporal;

/// <summary>
/// An RFC 6902 JSON Patch describing the change between two history versions of a <typeparamref name="T"/>,
/// as returned by <c>GetDiffBetweenAsync</c>. It is a self-contained type rather than ASP.NET Core's
/// <c>JsonPatchDocument</c> so the package carries no web dependency.
/// </summary>
/// <typeparam name="T">The entity type the patch was computed for.</typeparam>
public sealed class JsonPatchDocument<T> where T : class
{
    readonly JsonSerializerOptions? options;

    /// <summary>Creates a patch from a list of operations.</summary>
    /// <param name="operations">The operations, applied in order.</param>
    /// <param name="options">Serializer options used by <see cref="ApplyTo"/> when none are passed to it.</param>
    public JsonPatchDocument(IReadOnlyList<JsonPatchOperation> operations, JsonSerializerOptions? options = null)
    {
        this.Operations = operations;
        this.options = options;
    }

    /// <summary>The patch operations, in order.</summary>
    public IReadOnlyList<JsonPatchOperation> Operations { get; }

    /// <summary>
    /// Serializes the patch as an RFC 6902 JSON array (<c>[{"op":"replace","path":"/Status","value":"Shipped"}]</c>),
    /// ready to store, log or send to a client.
    /// </summary>
    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var op in this.Operations)
            {
                writer.WriteStartObject();
                writer.WriteString("op", op.Op);
                if (op.From != null)
                    writer.WriteString("from", op.From);
                writer.WriteString("path", op.Path);
                if (op.Value.HasValue)
                {
                    writer.WritePropertyName("value");
                    op.Value.Value.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Applies the patch to a copy of <paramref name="target"/> by round-tripping it through System.Text.Json, and
    /// returns the patched copy. Intended for plain entity shapes: an object graph with loaded navigations to other
    /// entities is serialized too, so pass options with a <c>ReferenceHandler</c> (or detach it) for such graphs.
    /// </summary>
    public T ApplyTo(T target, JsonSerializerOptions? options = null)
    {
        var opts = options ?? this.options ?? JsonSerializerOptions.Default;
        var node = JsonSerializer.SerializeToNode(target, opts)
            ?? throw new InvalidOperationException("Serialization produced null.");
        foreach (var op in this.Operations)
            ApplyOp(node, op);
        return JsonSerializer.Deserialize<T>(node, opts)
            ?? throw new InvalidOperationException("Deserialization produced null.");
    }

    static void ApplyOp(JsonNode root, JsonPatchOperation op)
    {
        var segments = ParsePath(op.Path);
        switch (op.Op)
        {
            case "add":
            case "replace":
                SetValue(root, segments, op.Value);
                break;

            case "remove":
                RemoveValue(root, segments);
                break;

            case "copy":
                var copied = GetValue(root, ParsePath(op.From!));
                SetValue(root, segments, copied != null ? JsonDocument.Parse(copied.ToJsonString()).RootElement.Clone() : null);
                break;

            case "move":
                var moved = GetValue(root, ParsePath(op.From!));
                RemoveValue(root, ParsePath(op.From!));
                SetValue(root, segments, moved != null ? JsonDocument.Parse(moved.ToJsonString()).RootElement.Clone() : null);
                break;

            case "test":
                var actual = GetValue(root, segments);
                var expected = op.Value.HasValue ? JsonNode.Parse(op.Value.Value.GetRawText()) : null;
                if (!JsonNode.DeepEquals(actual, expected))
                    throw new InvalidOperationException($"Test operation failed for path '{op.Path}'.");
                break;

            default:
                throw new NotSupportedException($"Unsupported patch operation: {op.Op}");
        }
    }

    static string[] ParsePath(string path)
    {
        if (String.IsNullOrEmpty(path) || path == "/")
            return [];

        // RFC 6901: drop the leading '/', split, then unescape each token ('~1' -> '/' before '~0' -> '~').
        var segments = path[1..].Split('/');
        for (var i = 0; i < segments.Length; i++)
            segments[i] = segments[i].Replace("~1", "/").Replace("~0", "~");
        return segments;
    }

    static JsonNode? GetValue(JsonNode root, string[] segments)
    {
        JsonNode? current = root;
        foreach (var seg in segments)
        {
            current = current switch
            {
                JsonObject obj => obj[seg],
                JsonArray arr when Int32.TryParse(seg, out var idx) => arr[idx],
                _ => null
            };
        }
        return current;
    }

    static void SetValue(JsonNode root, string[] segments, JsonElement? value)
    {
        if (segments.Length == 0)
            throw new InvalidOperationException("Cannot set the root node.");

        var parent = NavigateToParent(root, segments);
        var key = segments[^1];
        var nodeValue = value.HasValue ? JsonNode.Parse(value.Value.GetRawText()) : null;

        if (parent is JsonObject obj)
        {
            obj[key] = nodeValue;
        }
        else if (parent is JsonArray arr)
        {
            if (key == "-")
                arr.Add(nodeValue);
            else if (Int32.TryParse(key, out var idx) && idx == arr.Count)
                arr.Add(nodeValue);
            else if (Int32.TryParse(key, out idx))
                arr[idx] = nodeValue;
        }
    }

    static void RemoveValue(JsonNode root, string[] segments)
    {
        if (segments.Length == 0)
            throw new InvalidOperationException("Cannot remove the root node.");

        var parent = NavigateToParent(root, segments);
        var key = segments[^1];

        if (parent is JsonObject obj)
            obj.Remove(key);
        else if (parent is JsonArray arr && Int32.TryParse(key, out var idx))
            arr.RemoveAt(idx);
    }

    static JsonNode NavigateToParent(JsonNode root, string[] segments)
    {
        var current = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            var seg = segments[i];
            current = current switch
            {
                JsonObject obj => obj[seg] ?? throw new InvalidOperationException($"Path segment '{seg}' not found."),
                JsonArray arr when Int32.TryParse(seg, out var idx) => arr[idx] ?? throw new InvalidOperationException($"Array index '{idx}' not found."),
                _ => throw new InvalidOperationException($"Cannot navigate path segment '{seg}'.")
            };
        }
        return current;
    }
}
