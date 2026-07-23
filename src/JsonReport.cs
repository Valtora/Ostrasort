using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ostrasort;

/// <summary>
/// Machine-readable render of a full analysis (the --json flag): everything
/// the console report says, as one JSON document on stdout. Meant for
/// tooling - deploy pipelines, editors, agents - so the shape favours
/// stability: plain fields, lowercase enum names, nothing display-formatted.
/// </summary>
public static class JsonReport
{
    public static string Build(GameEnv env, EngineState s, string version, IReadOnlyList<string> performed)
    {
        var a = s.Analysis;

        var mods = new JsonArray();
        var pos = 0;
        foreach (var m in a.Registered)
            mods.Add(ModNode(m, ++pos, env));
        foreach (var m in a.UnregisteredLocal.Concat(a.UnregisteredWorkshop))
            mods.Add(ModNode(m, null, env));

        var collisions = new JsonArray();
        foreach (var c in a.Collisions)
        {
            var pairs = new JsonArray();
            foreach (var p in c.Pairs)
                pairs.Add(new JsonObject
                {
                    ["earlier"] = p.Earlier.DisplayName ?? p.Earlier.Name,
                    ["later"] = p.Later.DisplayName ?? p.Later.Name,
                    ["relation"] = p.Rel.ToString().ToLowerInvariant(),
                    ["lostFromEarlier"] = new JsonArray(p.LostFromEarlier.Select(x => (JsonNode)x).ToArray()),
                    ["addedByLater"] = new JsonArray(p.AddedByLater.Select(x => (JsonNode)x).ToArray()),
                });
            collisions.Add(new JsonObject
            {
                ["type"] = c.Type,
                ["strName"] = c.ObjName,
                ["friendlyName"] = c.FriendlyName,
                ["claimants"] = new JsonArray(c.Claimants.Select(m => (JsonNode)(m.DisplayName ?? m.Name)).ToArray()),
                ["resolvedByPatch"] = c.ResolvedByPatch,
                ["ffuMergedAtLoad"] = c.FfuMergedAtLoad,
                ["additiveAtLoad"] = c.AdditiveAtLoad,
                ["objectMergeable"] = c.ObjectMergeable,
                ["nothingLost"] = c.NothingLost,
                ["resolvedByOrder"] = c.ResolvedByOrder,
                // the same predicate the GUI badge and the exit code use, so
                // tooling never has to re-derive the needs-attention split
                ["needsAttention"] = CollisionView.NeedsAttention(c),
                ["pairs"] = pairs,
                ["fieldNotes"] = new JsonArray(c.FieldNotes.Select(n => (JsonNode)n).ToArray()),
            });
        }

        var root = new JsonObject
        {
            ["ostrasortVersion"] = version,
            ["game"] = new JsonObject
            {
                ["root"] = env.GameRoot,
                ["version"] = env.InstalledVersion,
                ["foundVia"] = env.DiscoveredVia,
                ["modsDir"] = env.ModsDir,
            },
            ["core"] = new JsonObject
            {
                ["objects"] = s.Scanner.CoreIndex.Count,
                ["types"] = s.Scanner.CoreTypes,
                ["problemFiles"] = s.Scanner.CoreProblemFiles,
            },
            ["ffu"] = a.Ffu is not { } ffu ? null : new JsonObject
            {
                ["summary"] = ffu.Summary,
                ["autoloaderActive"] = ffu.AutoloaderActive,
                ["frameworkPresent"] = ffu.FrameworkPresent,
                ["frameworkVersions"] = new JsonArray(ffu.FrameworkVersions.Select(v => (JsonNode)v).ToArray()),
                ["evidence"] = new JsonArray(ffu.Evidence.Select(e => (JsonNode)e).ToArray()),
            },
            ["ignorePatterns"] = new JsonArray(a.IgnorePatterns.Select(p => (JsonNode)p).ToArray()),
            ["profiles"] = new JsonArray(ProfileStore.List(env.LoadingOrderPath).Select(p => (JsonNode)ProfileNode(p)).ToArray()),
            ["mods"] = mods,
            ["collisions"] = collisions,
            ["patch"] = new JsonObject
            {
                ["exists"] = s.Patch.Exists,
                ["stale"] = s.Patch.Stale,
                ["staleReasons"] = new JsonArray(s.Patch.StaleReasons.Select(r => (JsonNode)r).ToArray()),
                ["obsolete"] = s.Patch.Obsolete,
                ["coveredKeys"] = new JsonArray(s.Patch.CoveredKeys.Select(k => (JsonNode)k).ToArray()),
                ["unneededKeys"] = new JsonArray(s.Patch.UnneededKeys.Select(k => (JsonNode)k).ToArray()),
                ["toolVersion"] = s.Patch.ToolVersion,
                ["excludedCount"] = s.Patch.ExcludedCount,
            },
            ["warnings"] = new JsonArray(a.Warnings.Select(w => (JsonNode)w).ToArray()),
            ["orderChanged"] = a.OrderChanged,
            ["suggestedOrder"] = new JsonArray(a.SuggestedOrder.Select(e => (JsonNode)e).ToArray()),
            ["changes"] = new JsonArray(a.Changes.Select(c => (JsonNode)new JsonObject
            {
                ["action"] = c.Action,
                ["entry"] = c.Entry,
                ["reason"] = c.Reason,
            }).ToArray()),
            ["performed"] = new JsonArray(performed.Select(p => (JsonNode)p).ToArray()),
            ["actionable"] = Engine.Actionable(s),
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>A saved profile's metadata (also used by --profile-list --json).</summary>
    public static JsonObject ProfileNode(Profile p) => new()
    {
        ["name"] = p.Name,
        ["savedAt"] = p.SavedAt,
        ["savedGameVersion"] = p.SavedGameVersion,
        ["mods"] = p.ModCount,
        ["entries"] = new JsonArray(p.Raws.Select(r => (JsonNode)r).ToArray()),
    };

    public static string ProfilesJson(IEnumerable<Profile> profiles) =>
        new JsonArray(profiles.Select(p => (JsonNode)ProfileNode(p)).ToArray())
            .ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    private static JsonObject ModNode(ModEntry m, int? position, GameEnv env)
    {
        var node = new JsonObject
        {
            ["position"] = position is { } p ? p : null,
            ["raw"] = m.Raw.Length > 0 ? m.Raw : null,
            ["name"] = m.Name,
            ["displayName"] = m.DisplayName,
            ["kind"] = m.Kind.ToString().ToLowerInvariant(),
            ["class"] = m.Kind == EntryKind.Core ? "core" : m.IsPatch ? "patch" : m.Class.ToString().ToLowerInvariant(),
            ["registered"] = m.Registered,
            ["disabled"] = m.Disabled,
            ["ignored"] = m.Ignored,
            ["dir"] = m.Dir,
            ["workshopId"] = m.WorkshopId,
            ["modVersion"] = m.ModVersion,
            ["gameVersion"] = m.GameVersion,
            ["gameVersionNote"] = m.GameVersionNote(env.InstalledVersion),
            ["dataObjects"] = m.DataObjects,
            ["coreOverrides"] = m.CoreOverrides,
            ["category"] = m.Kind == EntryKind.Core ? null : m.Category.ToString(),
            ["loadTier"] = m.Kind == EntryKind.Core ? null : m.Tier.ToString().ToLowerInvariant(),
            ["loadTierPinned"] = m.CategoryManual,
            ["isPatch"] = m.IsPatch,
            ["hasPlugins"] = m.HasPlugins,
            ["hasPatchers"] = m.HasPatchers,
            ["jsonErrors"] = new JsonArray(m.JsonErrors.Select(e => (JsonNode)e).ToArray()),
            ["logNotes"] = new JsonArray(m.LogNotes.Select(n => (JsonNode)n).ToArray()),
        };
        if (m.RemoveIds.Count > 0)
            node["removeIds"] = new JsonArray(m.RemoveIds.Select(r => (JsonNode)$"{r.Type}/{r.Id}").ToArray());
        if (m.IsFfu)
            node["ffu"] = new JsonObject
            {
                ["group"] = m.FfuGroup.ToString(),
                ["isPatchMod"] = m.IsFfuPatch,
                ["signals"] = new JsonArray(m.FfuSignals.Select(x => (JsonNode)x).ToArray()),
            };
        return node;
    }
}
