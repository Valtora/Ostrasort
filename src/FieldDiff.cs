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
        // with the FFU framework installed the GAME merges same-name objects
        // field-by-field at load - disjoint edits all apply, no patch needed
        var ffuMerge = a.Ffu is { FrameworkPresent: true };

        foreach (var col in a.Collisions)
        {
            // loot pools get item-level analysis + the patcher; skip them here
            if (col.Claimants.All(m => m.Claims.TryGetValue((col.Type, col.ObjName), out var l) && l is not null))
                continue;

            // a claimant editing via FFU --ADD--/--DEL--/... commands merges at
            // load; there is no whole-object comparison to make
            var editors = col.Claimants
                .Where(m => m.FfuArrayEditClaims.Contains((col.Type, col.ObjName)))
                .Select(m => m.DisplayName ?? m.Name).ToList();
            if (editors.Count > 0)
            {
                col.FfuMergedAtLoad = true;
                col.FieldNotes.Add($"{string.Join(", ", editors)} edits this object with FFU precision array " +
                                   "commands - the edit merges at load instead of replacing; it must load after " +
                                   "the versions it edits (Ostrasort keeps FFU mods after non-FFU mods)");
                continue;
            }

            // conditions defined via conditions_simple are parsed into the
            // conditions dictionary AFTER every mod loads (ParseConditionsSimple),
            // so they beat any conditions\ version of the same name regardless of
            // load order - there is no JSON object to field-merge either
            var simpleDefiners = col.Claimants
                .Where(m => m.SimpleConditionNames.Contains(col.ObjName))
                .Select(m => m.DisplayName ?? m.Name).ToList();
            if (col.Type == "conditions" && simpleDefiners.Count > 0)
            {
                col.FieldNotes.Add($"{string.Join(", ", simpleDefiners)} define(s) it via conditions_simple - " +
                                   "those are applied after every mod loads and override any conditions\\ version " +
                                   "of this name regardless of load order (among several conditions_simple " +
                                   "definitions, the last-loaded mod's wins)");
                continue;
            }

            var coreObj = LoadObject(Path.Combine(env.CoreDataDir, col.Type), col.ObjName, a.IgnorePatterns);
            var versions = new List<(ModEntry Mod, JsonNode Obj)>();
            foreach (var m in col.Claimants)
            {
                if (m.Dir is null) continue;
                var obj = LoadObject(Path.Combine(m.Dir, "data", col.Type), col.ObjName, a.IgnorePatterns);
                if (obj is not null) versions.Add((m, obj));
            }
            if (versions.Count < 2) continue;

            if (coreObj is null)
            {
                col.FfuMergedAtLoad = ffuMerge;
                col.FieldNotes.Add(ffuMerge
                    ? "no vanilla version to merge against — FFU merges the versions field-by-field at load (last loaded wins contested fields)"
                    : "no vanilla version to merge against — only the last-loaded mod's version applies");
                continue;
            }

            var plan = ObjectMerge.Build(col, coreObj, versions, ffuMerge);
            if (plan is null)
            {
                col.FieldNotes.Add("the mods' versions are identical — nothing is lost");
                continue;
            }

            var auto = plan.Fields.Where(f => !f.Contested).ToList();
            var conflicts = plan.Fields.Where(f => f.Contested).ToList();
            var modBySource = versions.ToDictionary(v => v.Mod.Dir ?? v.Mod.Raw, v => v.Mod, StringComparer.OrdinalIgnoreCase);
            bool NonFfuOption(MergeOption o) =>
                o.SourceId != "__union__" && modBySource.TryGetValue(o.SourceId, out var m) && !m.IsFfu;

            if (ffuMerge)
            {
                // the game itself keeps disjoint-field edits; a patch only adds
                // value for fields contested among NON-FFU mods (it loads after
                // the whole non-FFU block, and FFU mods win their fields anyway)
                col.ObjectMergeable = conflicts.Any(f => f.Options.All(NonFfuOption));
                col.FfuMergedAtLoad = !col.ObjectMergeable;

                if (auto.Count > 0)
                    col.FieldNotes.Add("merged by FFU at load — nothing lost: " + string.Join(", ",
                        auto.Select(f => $"{f.Token} (from {f.Options[0].SourceLabel})")));
                foreach (var f in conflicts)
                    col.FieldNotes.Add(f.Options.All(NonFfuOption)
                        ? $"conflict — you choose: {f.Token} (both non-FFU; the patch can enforce your pick)"
                        : $"conflict on {f.Token}: the last-loaded (FFU) mod's value wins that field at load");
                continue;
            }

            // merge beats last-wins when a field the last mod left at vanilla was
            // changed by an earlier mod, or any field is genuinely contested
            var lastId = versions[^1].Mod.Dir ?? versions[^1].Mod.Raw;
            col.ObjectMergeable = plan.Fields.Any(f =>
                f.Contested || !f.Options.Any(o => o.SourceId == lastId));

            if (auto.Count > 0)
                col.FieldNotes.Add("auto-merge: " + string.Join(", ",
                    auto.Select(f => $"{f.Token} (from {f.Options[0].SourceLabel})")));
            if (conflicts.Count > 0)
                col.FieldNotes.Add("conflict — you choose: " + string.Join(", ", conflicts.Select(f => f.Token)));
            if (!col.ObjectMergeable && conflicts.Count == 0)
                col.FieldNotes.Add("the last-loaded mod already includes every change — nothing lost");
        }
    }

    internal static JsonNode? LoadObject(string typeDir, string strName, IReadOnlyList<string>? ignorePatterns = null)
    {
        if (!Directory.Exists(typeDir)) return null;
        JsonNode? found = null;   // keep scanning: a later same-strName duplicate replaces the earlier (like the game)
        foreach (var file in Directory.EnumerateFiles(typeDir, "*.json", SearchOption.AllDirectories))
        {
            if (Scanner.IsIgnored(file, ignorePatterns, out _)) continue;   // the game skips these files
            // JsonNode materializes lazily: duplicate property names (which some
            // mods ship and the game's parser tolerates) throw ArgumentException
            // on first ACCESS, not at Parse - so the whole walk sits in the try
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(file), null, Lenient);
                var objects = root is JsonArray arr ? arr.ToList() : new List<JsonNode?> { root };
                foreach (var o in objects)
                    if (o?["strName"]?.GetValue<string>() == strName)
                        found = o;
            }
            catch (JsonException) { }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }
        return found;
    }
}
