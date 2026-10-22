using System.Text.Json;

namespace Shiny.EntityFrameworkCore.Temporal;

/// <summary>
/// One RFC 6902 JSON Patch operation. <see cref="Path"/> and <see cref="From"/> are RFC 6901 JSON Pointers
/// over the snapshot shape (EF property names; owned and complex values nest as objects, collections as arrays).
/// </summary>
public sealed class JsonPatchOperation
{
    /// <summary>The operation name: <c>add</c>, <c>remove</c>, <c>replace</c>, <c>copy</c>, <c>move</c> or <c>test</c>.</summary>
    public string Op { get; }

    /// <summary>The JSON Pointer the operation targets.</summary>
    public string Path { get; }

    /// <summary>The source pointer for <c>copy</c> / <c>move</c>; null otherwise.</summary>
    public string? From { get; }

    /// <summary>The value for <c>add</c> / <c>replace</c> / <c>test</c>; null for the others.</summary>
    public JsonElement? Value { get; }

    JsonPatchOperation(string op, string path, string? from, JsonElement? value)
    {
        this.Op = op;
        this.Path = path;
        this.From = from;
        this.Value = value;
    }

    /// <summary>Creates an <c>add</c> operation.</summary>
    public static JsonPatchOperation Add(string path, JsonElement? value) => new("add", path, null, value);

    /// <summary>Creates a <c>replace</c> operation.</summary>
    public static JsonPatchOperation Replace(string path, JsonElement? value) => new("replace", path, null, value);

    /// <summary>Creates a <c>remove</c> operation.</summary>
    public static JsonPatchOperation Remove(string path) => new("remove", path, null, null);

    /// <summary>Creates a <c>copy</c> operation.</summary>
    public static JsonPatchOperation Copy(string from, string path) => new("copy", path, from, null);

    /// <summary>Creates a <c>move</c> operation.</summary>
    public static JsonPatchOperation Move(string from, string path) => new("move", path, from, null);

    /// <summary>Creates a <c>test</c> operation.</summary>
    public static JsonPatchOperation Test(string path, JsonElement? value) => new("test", path, null, value);
}
