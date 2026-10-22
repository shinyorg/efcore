using System.Text.Json;
using System.Text.Json.Nodes;

namespace Shiny.EntityFrameworkCore.AI.Implementation;

/// <summary>
/// The JSON schemas the model sees, built from the EF-derived <see cref="EntityField"/> set: CLR type, nullability,
/// key, enum names, max length and description all come from the model. Field enums are closed — the model is shown
/// exactly the names it may use, and the translator refuses any other.
/// </summary>
static class SchemaBuilder
{
    static readonly string[] Operators = ["eq", "ne", "gt", "gte", "lt", "lte", "contains", "startsWith", "endsWith", "in"];

    public static JsonObject BuildFilterSchema(IReadOnlyList<EntityField> fields)
    {
        var filterDef = new JsonObject
        {
            ["type"] = "object",
            ["description"] =
                "Structured filter. Supply EITHER a leaf comparison ({field, op, value}) OR exactly one logical " +
                "combinator ({and:[...]}, {or:[...]}, {not:{...}}). " + DescribeFields(fields),
            ["properties"] = new JsonObject
            {
                ["field"] = BuildFieldSelector(fields, "Field name for a leaf comparison.", nullable: false),
                ["op"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray(Operators.Select(o => (JsonNode)JsonValue.Create(o)).ToArray()),
                    ["description"] =
                        "Comparison operator. 'in' takes an array value (max " + FilterTranslator.MaxInValues + "); " +
                        "'contains'/'startsWith'/'endsWith' require string fields; gt/gte/lt/lte need a number, date/time or string field."
                },
                ["value"] = new JsonObject
                {
                    ["description"] = "Comparison value, compatible with the field's type. Use an array for 'in'; null with eq/ne tests for null."
                },
                ["and"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["$ref"] = "#/$defs/filter" },
                    ["description"] = "All sub-filters must match."
                },
                ["or"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["$ref"] = "#/$defs/filter" },
                    ["description"] = "At least one sub-filter must match."
                },
                ["not"] = new JsonObject
                {
                    ["$ref"] = "#/$defs/filter",
                    ["description"] = "Negates the inner filter."
                }
            }
        };

        return new JsonObject
        {
            ["$defs"] = new JsonObject { ["filter"] = filterDef },
            ["$ref"] = "#/$defs/filter"
        };
    }

    /// <summary>The <c>get_by_id</c> / <c>delete</c> schema — one required id, typed like the key.</summary>
    public static JsonElement BuildIdSchema(EntityField key, string description)
    {
        var (type, format, _) = ScalarTypes.Classify(key.ClrType);
        var id = new JsonObject
        {
            ["type"] = type == "string" ? "string" : new JsonArray(type, "string"),
            ["description"] = description
        };
        if (format != null)
            id["format"] = format;

        return ToJsonElement(new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["id"] = id },
            ["required"] = new JsonArray("id")
        });
    }

    public static JsonElement BuildQuerySchema(IReadOnlyList<EntityField> fields, int maxPageSize)
        => ToJsonElement(new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["filter"] = BuildFilterSchema(fields),
                ["orderBy"] = BuildFieldSelector(fields, "Optional field to sort by. Ties (and no orderBy) fall back to the primary key.", nullable: true),
                ["orderDirection"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray("asc", "desc"),
                    ["description"] = "Sort direction. Defaults to 'asc'."
                },
                ["limit"] = new JsonObject
                {
                    ["type"] = "integer",
                    ["minimum"] = 1,
                    ["maximum"] = maxPageSize,
                    ["description"] = $"Maximum number of records to return (1-{maxPageSize}). Defaults to {Math.Min(50, maxPageSize)}."
                },
                ["offset"] = new JsonObject
                {
                    ["type"] = "integer",
                    ["minimum"] = 0,
                    ["description"] = "Number of records to skip. Defaults to 0."
                }
            }
        });

    public static JsonElement BuildCountSchema(IReadOnlyList<EntityField> fields)
        => ToJsonElement(new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["filter"] = BuildFilterSchema(fields) }
        });

    public static JsonElement BuildAggregateSchema(IReadOnlyList<EntityField> fields)
    {
        var aggregatable = fields.Where(f => ScalarTypes.IsOrderable(f.ClrType)).ToArray();
        return ToJsonElement(new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["function"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray("count", "sum", "min", "max", "avg"),
                    ["description"] = "Aggregate function. 'count' ignores 'field'; sum/avg need a numeric field; min/max a numeric or date/time field."
                },
                ["field"] = BuildFieldSelector(aggregatable, "Field to aggregate. Required for sum/min/max/avg.", nullable: true),
                ["filter"] = BuildFilterSchema(fields)
            },
            ["required"] = new JsonArray("function")
        });
    }

    /// <summary>The <c>insert</c> schema: every field the model may set, nested like the entity.</summary>
    public static JsonElement BuildInsertSchema(IReadOnlyList<EntityField> fields, string? description)
    {
        var writable = fields.Where(f => f.InsertWritable).ToArray();
        var document = BuildObject(writable, description);
        var required = writable
            .Where(f => !f.IsNullable && !f.Name.Contains('.') && (!f.ClrType.IsValueType || f.IsKey))
            .Select(f => (JsonNode)JsonValue.Create(f.Name))
            .ToArray();
        if (required.Length > 0)
            document["required"] = new JsonArray(required);

        return WrapDocument(document, "The record to insert. Fields the database generates (such as an identity key) are assigned on save.");
    }

    /// <summary>The <c>update</c> schema: the key (required) plus every field the model may change.</summary>
    public static JsonElement BuildUpdateSchema(IReadOnlyList<EntityField> fields, EntityField key, string? description)
    {
        var document = BuildObject(fields.Where(f => f.UpdateWritable || f == key).ToArray(), description);
        document["required"] = new JsonArray(key.Name);
        return WrapDocument(document,
            $"The changes to apply. '{key.Name}' identifies the record; every other field is optional, and fields you omit keep their stored values.");
    }

    /// <summary>The read shape of one record — what the query tool returns per item.</summary>
    public static JsonElement BuildEntitySchema(IReadOnlyList<EntityField> fields, string? description)
        => ToJsonElement(BuildObject(fields, description));

    static JsonElement WrapDocument(JsonObject document, string description)
    {
        document["description"] = document["description"] is { } existing
            ? $"{existing.GetValue<string>()} {description}"
            : description;

        return ToJsonElement(new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["document"] = document },
            ["required"] = new JsonArray("document")
        });
    }

    /// <summary>Nests dotted field names back into objects: <c>address.city</c> → <c>{ address: { city } }</c>.</summary>
    static JsonObject BuildObject(IReadOnlyList<EntityField> fields, string? description)
    {
        var root = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
        if (!string.IsNullOrWhiteSpace(description))
            root["description"] = description;

        foreach (var field in fields)
        {
            var segments = field.Name.Split('.');
            var properties = (JsonObject)root["properties"]!;
            for (var i = 0; i < segments.Length - 1; i++)
            {
                if (properties[segments[i]] is not JsonObject nested)
                {
                    nested = new JsonObject { ["type"] = new JsonArray("object", "null"), ["properties"] = new JsonObject() };
                    properties[segments[i]] = nested;
                }
                properties = (JsonObject)nested["properties"]!;
            }
            properties[segments[^1]] = BuildFieldSchema(field);
        }
        return root;
    }

    public static JsonObject BuildFieldSchema(EntityField field)
    {
        var (type, format, values) = ScalarTypes.Classify(field.ClrType);
        var node = new JsonObject
        {
            ["type"] = field.IsNullable ? new JsonArray(type, "null") : type
        };

        if (format != null)
            node["format"] = format;
        if (values != null)
        {
            var allowed = new List<JsonNode?>(values.Select(v => (JsonNode?)JsonValue.Create(v)));
            if (field.IsNullable)
                allowed.Add(null);
            node["enum"] = new JsonArray(allowed.ToArray());
        }
        if (field.MaxLength is { } max && field.ClrType == typeof(string))
            node["maxLength"] = max;

        var description = field.IsKey
            ? string.IsNullOrWhiteSpace(field.Description) ? "Primary key." : $"Primary key. {field.Description}"
            : field.Description;
        if (!string.IsNullOrWhiteSpace(description))
            node["description"] = description;

        return node;
    }

    static JsonObject BuildFieldSelector(IReadOnlyList<EntityField> fields, string description, bool nullable)
    {
        var values = new List<JsonNode?>();
        if (nullable)
            values.Add(null);
        foreach (var f in fields)
            values.Add(JsonValue.Create(f.Name));

        return new JsonObject
        {
            ["type"] = nullable ? new JsonArray("string", "null") : "string",
            ["enum"] = new JsonArray(values.ToArray()),
            ["description"] = description
        };
    }

    static string DescribeFields(IReadOnlyList<EntityField> fields)
    {
        var sb = new System.Text.StringBuilder("Allowed fields: ");
        for (var i = 0; i < fields.Count; i++)
        {
            if (i > 0)
                sb.Append(", ");
            sb.Append(fields[i].Name).Append(" (").Append(ScalarTypes.Classify(fields[i].ClrType).Type);
            if (!string.IsNullOrWhiteSpace(fields[i].Description))
                sb.Append(": ").Append(fields[i].Description);
            sb.Append(')');
        }
        return sb.ToString();
    }

    public static JsonElement ToJsonElement(JsonObject node)
    {
        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }
}
