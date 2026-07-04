using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ostrasort;

/// <summary>
/// Field-level analysis for non-loot same-object collisions, driven by the same
/// <see cref="ObjectMerge"/> engine that generates the merge. The game replaces
/// whole objects (last loaded wins), but when two mods change different fields
/// of one object a 3-way merge (core = base) can keep both - this flags those
/// collisions as mergeable and notes what auto-merges vs what needs a decision.
/// </summary>
public static class FieldDiff
{
    private static readonly JsonDocumentOptions Lenient = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>Annotates every non-loot collision with merge feasibility + field notes.</summary>
    public static void Annotate(GameEnv env, Analysis a)
    {
        foreach (var col in a.Collisions)
        {
            // loot pools get item-level analysis + the patcher; skip them here
            if (col.Claimants.All(m => m.Claims.TryGetValue((col.Type, col.ObjName), out var l) && l is not null))
                continue;

            var coreObj = LoadObject(Path.Combine(env.CoreDataDir, col.Type), col.ObjName);
            var versions = new List<(ModEntry Mod, JsonNode Obj)>();
            foreach (var m in col.Claimants)
            {
                if (m.Dir is null) continue;
                var obj = LoadObject(Path.Combine(m.Dir, "data", col.Type), col.ObjName);
                if (obj is not null) versions.Add((m, obj));
            }
            if (versions.Count < 2) continue;

            if (coreObj is null)
            {
                col.FieldNotes.Add("no vanilla version to merge against — only the last-loaded mod's version applies");
                continue;
            }

            var plan = ObjectMerge.Build(col, coreObj, versions);
            if (plan is null)
            {
                col.FieldNotes.Add("the mods' versions are identical — nothing is lost");
                continue;
            }

            // merge beats last-wins when a field the last mod left at vanilla was
            // changed by an earlier mod, or any field is genuinely contested
            var lastId = versions[^1].Mod.Dir ?? versions[^1].Mod.Raw;
            col.ObjectMergeable = plan.Fields.Any(f =>
                f.Contested || !f.Options.Any(o => o.SourceId == lastId));

            var auto = plan.Fields.Where(f => !f.Contested).ToList();
            var conflicts = plan.Fields.Where(f => f.Contested).ToList();
            if (auto.Count > 0)
                col.FieldNotes.Add("auto-merge: " + string.Join(", ",
                    auto.Select(f => $"{f.Token} (from {f.Options[0].SourceLabel})")));
            if (conflicts.Count > 0)
                col.FieldNotes.Add("conflict — you choose: " + string.Join(", ", conflicts.Select(f => f.Token)));
            if (!col.ObjectMergeable && conflicts.Count == 0)
                col.FieldNotes.Add("the last-loaded mod already includes every change — nothing lost");
        }
    }

    internal static JsonNode? LoadObject(string typeDir, string strName)
    {
        if (!Directory.Exists(typeDir)) return null;
        foreach (var file in Directory.EnumerateFiles(typeDir, "*.json", SearchOption.AllDirectories))
        {
            // JsonNode materializes lazily: duplicate property names (which some
            // mods ship and the game's parser tolerates) throw ArgumentException
            // on first ACCESS, not at Parse - so the whole walk sits in the try
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(file), null, Lenient);
                var objects = root is JsonArray arr ? arr.ToList() : new List<JsonNode?> { root };
                foreach (var o in objects)
                    if (o?["strName"]?.GetValue<string>() == strName)
                        return o;
            }
            catch (JsonException) { }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }
        return null;
    }
}
