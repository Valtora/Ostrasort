using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ostrasort;

/// <summary>
/// Field-level 3-way merge for non-loot object collisions. The base game's
/// version is the common ancestor; each conflicting mod is a branch. A field
/// only one mod changed (vs core) merges automatically; a field several mods
/// changed to different values is a conflict the player resolves. Array (a*)
/// fields additionally offer a union of both mods' entries.
///
/// This produces a structurally valid merge, not a guaranteed-correct one:
/// mods may change interdependent fields the merge can't reason about, so the
/// result is schema-validated and always presented as best-effort to review.
/// </summary>
public static class ObjectMerge
{
    /// <summary>
    /// Builds the field-merge plan for one collision. Requires a core base
    /// (three-way) - collisions on mod-added objects with no vanilla ancestor
    /// are left for the report, not auto-merged. Returns null if there is
    /// nothing to merge (identical overrides).
    /// </summary>
    public static ObjectPlan? Build(Collision c, JsonNode coreBase, List<(ModEntry Mod, JsonNode Obj)> versions)
    {
        if (versions.Count < 2) return null;

        var fields = new List<MergeItem>();

        // every field key seen in core or any mod version, except the identity
        var keys = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void AddKeys(JsonNode n)
        {
            if (n is JsonObject o)
                foreach (var (k, _) in o)
                    if (k != "strName" && seen.Add(k)) keys.Add(k);
        }
        AddKeys(coreBase);
        foreach (var v in versions) AddKeys(v.Obj);

        foreach (var key in keys)
        {
            var baseVal = (coreBase as JsonObject)?[key];
            // each mod's value for this key, and whether it differs from core
            var changers = versions
                .Select(v => (v.Mod, Val: (v.Obj as JsonObject)?[key]))
                .Where(x => !NodeEquals(x.Val, baseVal))
                .ToList();
            if (changers.Count == 0) continue;   // untouched vs core -> stays as base

            var options = changers
                .Select(x => new MergeOption(SourceId(x.Mod), x.Mod.DisplayName ?? x.Mod.Name,
                    Compact(x.Val), x.Val is null ? null : x.Val.DeepClone()))
                .ToList();

            var isArray = key.StartsWith("a", StringComparison.Ordinal) && LooksArray(baseVal, changers);
            var distinctVals = options.Select(o => o.Entry).Distinct(StringComparer.Ordinal).Count();

            // array conflict: offer the union of everyone's entries as an extra choice
            if (distinctVals > 1 && isArray)
            {
                var union = UnionArrays(baseVal, changers.Select(x => x.Val));
                options.Add(new MergeOption("__union__", "union of both", Compact(union), union));
            }

            var item = new MergeItem
            {
                Token = key,
                Options = options,
                BaseNode = baseVal?.DeepClone(),
                IsArrayField = isArray,
            };
            // a single distinct changed value (one mod changed it, or all agree)
            // is an unambiguous merge - resolve it silently, no decision needed
            if (distinctVals == 1) item.ChosenSourceId = options[0].SourceId;

            fields.Add(item);
        }

        if (fields.Count == 0) return null;   // identical overrides - nothing to merge

        return new ObjectPlan
        {
            Collision = c,
            Type = c.Type,
            BaseObject = coreBase.DeepClone(),
            Fields = fields,
        };
    }

    /// <summary>Assembles the merged object from a fully decided plan.</summary>
    public static JsonNode Assemble(ObjectPlan plan)
    {
        var merged = plan.BaseObject.DeepClone().AsObject();
        merged["strName"] = plan.Collision.ObjName;

        foreach (var f in plan.Fields)
        {
            if (f.Excluded)
            {
                if (f.BaseNode is null) merged.Remove(f.Token);
                else merged[f.Token] = f.BaseNode.DeepClone();
                continue;
            }
            var val = f.Resolved.Node;
            if (val is null) merged.Remove(f.Token);          // a removal option won
            else merged[f.Token] = val.DeepClone();
        }
        return merged;
    }

    // ------------------------------------------------------------- helpers ---

    private static string SourceId(ModEntry m) => m.Dir ?? m.Raw;

    private static bool NodeEquals(JsonNode? a, JsonNode? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        return JsonNode.DeepEquals(a, b);
    }

    private static bool LooksArray(JsonNode? baseVal, List<(ModEntry Mod, JsonNode? Val)> changers) =>
        baseVal is JsonArray || changers.Any(x => x.Val is JsonArray);

    private static JsonArray UnionArrays(JsonNode? baseVal, IEnumerable<JsonNode?> vals)
    {
        var union = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Take(JsonNode? n)
        {
            if (n is not JsonArray arr) return;
            foreach (var el in arr)
            {
                var key = el?.ToJsonString() ?? "null";
                if (seen.Add(key)) union.Add(el is null ? null : el.DeepClone());
            }
        }
        Take(baseVal);
        foreach (var v in vals) Take(v);
        return union;
    }

    /// <summary>A short, human-readable rendering of a field value for the resolver.</summary>
    public static string Compact(JsonNode? n)
    {
        switch (n)
        {
            case null: return "(removed)";
            case JsonArray a:
                var items = a.Take(3).Select(e => Scalar(e));
                return $"[{string.Join(", ", items)}{(a.Count > 3 ? $", +{a.Count - 3}" : "")}]  ({a.Count} entries)";
            case JsonObject o:
                return $"{{{o.Count} field(s)}}";
            default:
                return Scalar(n);
        }
    }

    private static string Scalar(JsonNode? n)
    {
        if (n is null) return "null";
        var raw = n.ToJsonString();
        return raw.Length > 40 ? raw[..37] + "…" : raw;
    }
}
