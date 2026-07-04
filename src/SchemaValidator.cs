using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Ostrasort;

/// <summary>
/// A compact JSON-Schema (draft-07 subset) validator for the handful of schema
/// files the game ships in StreamingAssets\data\schemas. Those schemas use
/// only: type, properties, additionalProperties, enum, required, array items,
/// and string pattern - no $ref / oneOf / allOf - so this stays small and the
/// project stays dependency-free. Used as a safety net on the objects the
/// conflict merger writes into the Ostrasort Patch.
/// </summary>
public sealed class SchemaValidator
{
    // data type -> schema file basename (only these types have a schema)
    private static readonly Dictionary<string, string> SchemaForType =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["conditions"] = "conditions-schema.json",
            ["conditions_simple"] = "conditions-schema.json",
            ["condowners"] = "condowners-schema.json",
            ["interactions"] = "interactions-schema.json",
            ["items"] = "items-schema.json",
            ["plots"] = "plot-schema.json",
            ["plot_beats"] = "plot-schema.json",
        };

    private readonly string _schemaDir;
    private readonly Dictionary<string, JsonElement?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public SchemaValidator(GameEnv env) =>
        _schemaDir = Path.Combine(env.CoreDataDir, "schemas");

    public bool HasSchemaFor(string type) => SchemaForType.ContainsKey(type);

    /// <summary>
    /// Validates one object against its type's schema. Returns an empty list if
    /// valid or if there is no schema for the type (nothing to check against).
    /// </summary>
    public List<string> Validate(JsonNode obj, string type)
    {
        var errors = new List<string>();
        var itemSchema = ItemSchema(type);
        if (itemSchema is null) return errors;   // no schema for this type -> not validated
        var node = JsonNode.Parse(obj.ToJsonString());   // to JsonElement world
        using var doc = JsonDocument.Parse(node!.ToJsonString());
        ValidateNode(doc.RootElement, itemSchema.Value, "", errors);
        return errors;
    }

    /// <summary>The `items` subschema (schemas are array-of-objects); null if unavailable.</summary>
    private JsonElement? ItemSchema(string type)
    {
        if (!SchemaForType.TryGetValue(type, out var file)) return null;
        if (_cache.TryGetValue(file, out var cached)) return cached;

        JsonElement? result = null;
        var path = Path.Combine(_schemaDir, file);
        if (File.Exists(path))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("items", out var items))
                    result = items.Clone();
            }
            catch (JsonException) { /* unreadable schema -> treat as no schema */ }
        }
        _cache[file] = result;
        return result;
    }

    private static void ValidateNode(JsonElement value, JsonElement schema, string path, List<string> errors)
    {
        string Where() => string.IsNullOrEmpty(path) ? "(root)" : path;

        // type
        if (schema.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String)
        {
            var t = typeEl.GetString()!;
            if (!MatchesType(value, t))
            {
                errors.Add($"{Where()}: expected {t}, got {value.ValueKind.ToString().ToLowerInvariant()}");
                return;   // type mismatch - deeper checks are moot
            }
        }

        // enum
        if (schema.TryGetProperty("enum", out var enumEl) && enumEl.ValueKind == JsonValueKind.Array)
        {
            var ok = enumEl.EnumerateArray().Any(e => JsonEquals(e, value));
            if (!ok)
                errors.Add($"{Where()}: value {Compact(value)} is not one of the allowed values " +
                           $"[{string.Join(", ", enumEl.EnumerateArray().Select(Compact))}]");
        }

        // string pattern
        if (value.ValueKind == JsonValueKind.String &&
            schema.TryGetProperty("pattern", out var patEl) && patEl.ValueKind == JsonValueKind.String)
        {
            try
            {
                if (!Regex.IsMatch(value.GetString()!, patEl.GetString()!))
                    errors.Add($"{Where()}: \"{value.GetString()}\" does not match pattern /{patEl.GetString()}/");
            }
            catch (ArgumentException) { /* bad pattern in schema - ignore */ }
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            var props = schema.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object
                ? p : (JsonElement?)null;

            // required
            if (schema.TryGetProperty("required", out var reqEl) && reqEl.ValueKind == JsonValueKind.Array)
                foreach (var r in reqEl.EnumerateArray())
                    if (r.ValueKind == JsonValueKind.String && !value.TryGetProperty(r.GetString()!, out _))
                        errors.Add($"{Where()}: missing required property \"{r.GetString()}\"");

            // additionalProperties: false -> reject unknown keys
            var allowExtra = !schema.TryGetProperty("additionalProperties", out var apEl)
                             || apEl.ValueKind != JsonValueKind.False;

            foreach (var member in value.EnumerateObject())
            {
                if (props is { } pr && pr.TryGetProperty(member.Name, out var propSchema))
                    ValidateNode(member.Value, propSchema, path == "" ? member.Name : $"{path}.{member.Name}", errors);
                else if (!allowExtra)
                    errors.Add($"{Where()}: unknown property \"{member.Name}\" (not allowed by the schema)");
            }
        }
        else if (value.ValueKind == JsonValueKind.Array &&
                 schema.TryGetProperty("items", out var itemsSchema) && itemsSchema.ValueKind == JsonValueKind.Object)
        {
            var i = 0;
            foreach (var el in value.EnumerateArray())
                ValidateNode(el, itemsSchema, $"{path}[{i++}]", errors);
        }
    }

    private static bool MatchesType(JsonElement v, string t) => t switch
    {
        "string" => v.ValueKind == JsonValueKind.String,
        "boolean" => v.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "object" => v.ValueKind == JsonValueKind.Object,
        "array" => v.ValueKind == JsonValueKind.Array,
        "integer" => v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out _),
        "number" => v.ValueKind == JsonValueKind.Number,
        "null" => v.ValueKind == JsonValueKind.Null,
        _ => true,
    };

    private static bool JsonEquals(JsonElement a, JsonElement b) =>
        a.ValueKind == b.ValueKind && Compact(a) == Compact(b);

    private static string Compact(JsonElement e) => e.ValueKind == JsonValueKind.String
        ? $"\"{e.GetString()}\""
        : e.GetRawText();
}
