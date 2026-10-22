using System.Text.Json;
using System.Text.Json.Nodes;

namespace Shiny.EntityFrameworkCore.AI.Implementation;

/// <summary>
/// Moves records between the model's JSON and the entity, field by field. Only visible fields are ever written out or
/// read in — there is no general-purpose serializer in the path that could leak a hidden property or accept one.
/// </summary>
static class EntityJson
{
    public const int MaxNesting = 8;

    /// <summary>Builds one record from the projected column values, nesting dotted names back into objects.</summary>
    public static JsonObject FromValues(IReadOnlyList<EntityField> fields, object?[] values)
    {
        var root = new JsonObject();
        for (var i = 0; i < fields.Count; i++)
            Set(root, fields[i].Name, ScalarTypes.ToJson(values[i]));
        return root;
    }

    /// <summary>Builds one record from a materialized entity (the write tools echo what they saved).</summary>
    public static JsonObject FromEntity(IReadOnlyList<EntityField> fields, object entity)
    {
        var values = new object?[fields.Count];
        for (var i = 0; i < fields.Count; i++)
            values[i] = fields[i].GetValue(entity);
        return FromValues(fields, values);
    }

    static void Set(JsonObject root, string name, JsonNode? value)
    {
        var segments = name.Split('.');
        var current = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (current[segments[i]] is not JsonObject nested)
            {
                nested = new JsonObject();
                current[segments[i]] = nested;
            }
            current = nested;
        }
        current[segments[^1]] = value;
    }

    /// <summary>
    /// Reads the field assignments out of a model-supplied record body. Every property must name a visible field
    /// the operation may set; an unknown or hidden name and a read-only one are refused (with different messages —
    /// the model can see read-only fields, so naming them leaks nothing).
    /// </summary>
    /// <param name="document">The JSON object the model sent.</param>
    /// <param name="fields">The visible fields, by dotted name.</param>
    /// <param name="writable">Whether the operation may set a field.</param>
    /// <param name="identity">A field accepted (and returned separately) but never written — the key on update.</param>
    public static (List<(EntityField Field, object? Value)> Assignments, object? Identity) ReadAssignments(
        JsonElement document,
        IReadOnlyDictionary<string, EntityField> fields,
        Func<EntityField, bool> writable,
        EntityField? identity)
    {
        if (document.ValueKind != JsonValueKind.Object)
            throw new EntityAIToolException("'document' must be a JSON object.");

        var assignments = new List<(EntityField, object?)>();
        object? identityValue = null;
        Walk(document, "", 0);
        return (assignments, identityValue);

        void Walk(JsonElement obj, string prefix, int depth)
        {
            if (depth > MaxNesting)
                throw new EntityAIToolException("'document' is nested too deeply.");

            foreach (var property in obj.EnumerateObject())
            {
                var name = prefix + property.Name;
                var isGroup = property.Value.ValueKind == JsonValueKind.Object
                    && fields.Keys.Any(k => k.StartsWith(name + ".", StringComparison.Ordinal));

                if (isGroup)
                {
                    Walk(property.Value, name + ".", depth + 1);
                }
                else
                {
                    var field = FilterTranslator.Resolve(fields, name);
                    var value = ScalarTypes.FromJson(property.Value, field.ClrType, field.Name);
                    if (field == identity)
                        identityValue = value;
                    else if (writable(field))
                        assignments.Add((field, value));
                    else
                        throw new EntityAIToolException($"Field '{field.Name}' is read-only and cannot be set by this tool.");
                }
            }
        }
    }
}
