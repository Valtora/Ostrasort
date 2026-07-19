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
    /// Builds the field-merge plan for one collision. With a core object as
    /// <paramref name="coreBase"/> this is a 3-way merge; callers pass an empty
    /// object for a mod-added object with no vanilla ancestor, which degrades
    /// cleanly to a two-way merge (every mod field reads as a change vs the
    /// empty base, so fields only one mod sets auto-merge and disagreements are
    /// contested). Returns null if there is nothing to merge (identical overrides).
    ///
    /// FFU semantics: an FFU-style mod ships partial objects (fragments) that
    /// the FFU loader merges field-by-field - a field ABSENT from its object is
    /// untouched, not removed. With <paramref name="ffuFieldMerge"/> (the FFU
    /// framework is installed) that reading applies to every version, matching
    /// what the game itself will do at load; otherwise it applies only to
    /// versions from FFU-classified mods.
    /// </summary>
    public static ObjectPlan? Build(Collision c, JsonNode coreBase, List<(ModEntry Mod, JsonNode Obj)> versions,
                                    bool ffuFieldMerge = false)
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
            // each mod's value for this key, and whether it differs from core;
            // for fragment-style versions an absent key means "untouched"
            var changers = versions
                .Select(v => (v.Mod, Has: (v.Obj as JsonObject)?.ContainsKey(key) == true, Val: (v.Obj as JsonObject)?[key]))
                .Where(x => x.Has || !(ffuFieldMerge || x.Mod.IsFfu))
                .Select(x => (x.Mod, x.Val))
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
                var union = UnionArrays(changers.Select(x => x.Val));
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

    /// <summary>
    /// Union of the MODS' entries only. The base array is deliberately not
    /// seeded in: an entry present in core but absent from every mod's version
    /// was removed by all of them, and resurrecting it would override their
    /// shared intent. Entries removed by only one mod survive via the other's array.
    /// </summary>
    private static JsonArray UnionArrays(IEnumerable<JsonNode?> vals)
    {
        var union = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var v in vals)
        {
            if (v is not JsonArray arr) continue;
            foreach (var el in arr)
            {
                var key = el?.ToJsonString() ?? "null";
                if (seen.Add(key)) union.Add(el is null ? null : el.DeepClone());
            }
        }
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
