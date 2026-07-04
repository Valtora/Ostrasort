using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ostrasort;

/// <summary>
/// Field-level analysis for non-loot same-object collisions. The game replaces
/// whole objects, so when two mods override the same strName only the later
/// one survives - but WHICH fields each mod actually changes (vs core) tells
/// the user whether that loss matters: disjoint field sets mean a hand-merged
/// override could keep both, overlapping sets are a genuine tuning conflict.
/// </summary>
public static class FieldDiff
{
    private static readonly JsonDocumentOptions Lenient = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>Annotates every collision that has non-loot claims with field notes.</summary>
    public static void Annotate(GameEnv env, Analysis a)
    {
        foreach (var col in a.Collisions)
        {
            // loot pools get item-level analysis + the patcher; skip them here
            if (col.Claimants.All(m => m.Claims.TryGetValue((col.Type, col.ObjName), out var l) && l is not null))
                continue;

            var coreObj = LoadObject(Path.Combine(env.CoreDataDir, col.Type), col.ObjName);
            var changed = new List<(ModEntry Mod, HashSet<string> Fields)>();
            foreach (var m in col.Claimants)
            {
                var obj = LoadObject(Path.Combine(m.Dir!, "data", col.Type), col.ObjName);
                if (obj is null) continue;
                changed.Add((m, ChangedFields(coreObj, obj)));
            }
            if (changed.Count < 2) continue;

            foreach (var (m, fields) in changed)
                col.FieldNotes.Add($"{m.DisplayName ?? m.Name} changes: " +
                    (fields.Count == 0 ? "(nothing vs core - identical copy)" : string.Join(", ", fields.OrderBy(x => x))));

            var overlaps = changed[0].Fields.ToHashSet(StringComparer.Ordinal);
            for (var i = 1; i < changed.Count; i++) overlaps.IntersectWith(changed[i].Fields);
            col.FieldNotes.Add(overlaps.Count == 0
                ? "disjoint fields - a hand-merged override could keep every change (only the last-loaded survives today)"
                : $"BOTH change: {string.Join(", ", overlaps.OrderBy(x => x))} - genuine conflict, last loaded wins those");
        }
    }

    /// <summary>Top-level properties where the mod's object differs from core (added, modified, or removed).</summary>
    private static HashSet<string> ChangedFields(JsonNode? core, JsonNode mod)
    {
        var fields = new HashSet<string>(StringComparer.Ordinal);
        if (mod is not JsonObject mo) return fields;
        var co = core as JsonObject;

        foreach (var (name, value) in mo)
        {
            if (name == "strName") continue;
            var coreVal = co?[name];
            if (coreVal is null && value is null) continue;
            if (coreVal is null || value is null || !JsonNode.DeepEquals(coreVal, value)) fields.Add(name);
        }
        if (co is not null)
            foreach (var (name, _) in co)
                if (name != "strName" && !mo.ContainsKey(name)) fields.Add($"{name} (removed)");
        return fields;
    }

    private static JsonNode? LoadObject(string typeDir, string strName)
    {
        if (!Directory.Exists(typeDir)) return null;
        foreach (var file in Directory.EnumerateFiles(typeDir, "*.json", SearchOption.AllDirectories))
        {
            JsonNode? root;
            try { root = JsonNode.Parse(File.ReadAllText(file), null, Lenient); }
            catch (JsonException) { continue; }
            var objects = root is JsonArray arr ? arr.ToList() : new List<JsonNode?> { root };
            foreach (var o in objects)
                if (o?["strName"]?.GetValue<string>() == strName)
                    return o;
        }
        return null;
    }
}
