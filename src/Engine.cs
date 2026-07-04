using System.IO;

namespace Ostrasort;

public sealed record EngineState(LoadOrderFile Lo, Scanner Scanner, Analysis Analysis, PatchInfo Patch);

/// <summary>One full read-only pass over the install - shared by the console paths and the GUI.</summary>
public static class Engine
{
    public static EngineState Analyze(GameEnv env, bool tidy = false)
    {
        var lo = LoadOrderFile.Read(env.LoadingOrderPath);
        var analysis = new Analysis
        {
            Registered = lo.Order.Select(raw => ModEntry.Parse(raw, env)).ToList(),
        };
        DiscoverUnregistered(env, analysis);

        var scanner = new Scanner(env);
        scanner.IndexCore();
        foreach (var m in analysis.AllMods) scanner.Scan(m);

        analysis.FindCollisions();
        FieldDiff.Annotate(env, analysis);
        CheckImages(analysis);
        CheckBepInEx(env, analysis);
        var patch = Patcher.Inspect(env, analysis);   // marks collisions the patch resolves
        analysis.BuildSuggestion(tidy);
        return new EngineState(lo, scanner, analysis, patch);
    }

    /// <summary>Two mods shipping the same relative image path - last loaded wins the whole file.</summary>
    private static void CheckImages(Analysis a)
    {
        var mods = a.AllMods.Where(m => m.Dir is not null && !m.IsPatch && m.ImagePaths.Count > 0).ToList();
        var byPath = new Dictionary<string, List<ModEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in mods)
            foreach (var p in m.ImagePaths)
            {
                if (!byPath.TryGetValue(p, out var list)) byPath[p] = list = new();
                list.Add(m);
            }
        foreach (var (path, claimants) in byPath.Where(kv => kv.Value.Count > 1).OrderBy(kv => kv.Key))
            a.Warnings.Add($"image images\\{path} shipped by {string.Join(" THEN ", claimants.Select(m => m.DisplayName ?? m.Name))} " +
                           "- the last loaded copy fully replaces the others");
    }

    /// <summary>
    /// BepInEx sanity: plugins that can never load (no loader installed), and
    /// two different mods shipping the same plugin DLL name (double-patching).
    /// </summary>
    private static void CheckBepInEx(GameEnv env, Analysis a)
    {
        var modsWithPlugins = a.AllMods.Where(m => m.HasPlugins && !m.HasPatchers).ToList();
        var loaderInstalled = Directory.Exists(Path.Combine(env.GameRoot, "BepInEx", "core"))
                           || File.Exists(Path.Combine(env.GameRoot, "winhttp.dll"));
        var bridgePresent = a.AllMods.Any(m => m.HasPatchers)
                         || Directory.Exists(Path.Combine(env.GameRoot, "BepInEx", "patchers"));
        if (modsWithPlugins.Count > 0 && !loaderInstalled)
            a.Warnings.Add($"{modsWithPlugins.Count} mod(s) ship BepInEx plugins but BepInEx is NOT installed in the game folder " +
                           "- none of their code will load (subscribe to the BepInEx Mod Loader and finish its setup)");
        else if (modsWithPlugins.Any(m => m.Kind == EntryKind.Workshop) && !bridgePresent)
            a.Warnings.Add("Workshop code mods are subscribed but the Workshop bridge patcher is missing " +
                           "- their plugins will not load (subscribe to the BepInEx Mod Loader)");

        // same DLL name from two different owners = the same plugin patching twice
        var owners = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in a.AllMods.Where(m => m.PluginDlls.Count > 0))
            foreach (var dll in m.PluginDlls)
            {
                if (!owners.TryGetValue(dll, out var set)) owners[dll] = set = new(StringComparer.OrdinalIgnoreCase);
                set.Add(m.Name);
            }
        var gamePlugins = Path.Combine(env.GameRoot, "BepInEx", "plugins");
        if (Directory.Exists(gamePlugins))
            foreach (var f in Directory.EnumerateFiles(gamePlugins, "*.dll", SearchOption.AllDirectories))
            {
                // attribute manually deployed DLLs to their top-level folder so a local
                // mod's staged Workshop bundle is not double-counted against itself
                var rel = Path.GetRelativePath(gamePlugins, f);
                var owner = rel.Contains(Path.DirectorySeparatorChar) ? rel.Split(Path.DirectorySeparatorChar)[0] : "(plugins root)";
                if (owner.Equals("Workshop", StringComparison.OrdinalIgnoreCase)) continue;   // bridge-managed copies of subscribed mods
                var dll = Path.GetFileName(f);
                if (!owners.TryGetValue(dll, out var set)) owners[dll] = set = new(StringComparer.OrdinalIgnoreCase);
                set.Add(owner);
            }
        foreach (var (dll, who) in owners.Where(kv => kv.Value.Count > 1).OrderBy(kv => kv.Key))
            a.Warnings.Add($"plugin {dll} is shipped by more than one source ({string.Join(", ", who)}) " +
                           "- the same plugin loading twice usually breaks its Harmony patches");
    }

    public static bool Actionable(EngineState s) =>
        s.Analysis.OrderChanged
        || s.Analysis.HasUnresolvedConflicts
        || s.Patch.Stale
        || s.Patch.Obsolete;

    /// <summary>
    /// Finds mods that exist on disk but are missing from aLoadOrder: local
    /// folders under Mods\ (invisible to the in-game MODS screen) and
    /// subscribed Workshop items (the game re-adds those itself on launch;
    /// Ostrasort just surfaces them).
    /// </summary>
    private static void DiscoverUnregistered(GameEnv env, Analysis a)
    {
        var localNames = a.Registered.Where(m => m.Kind == EntryKind.Local)
            .Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(env.ModsDir))
        {
            foreach (var dir in Directory.EnumerateDirectories(env.ModsDir))
            {
                var name = Path.GetFileName(dir);
                if (localNames.Contains(name)) continue;
                if (!File.Exists(Path.Combine(dir, "mod_info.json"))) continue;   // not a mod folder
                a.UnregisteredLocal.Add(new ModEntry
                {
                    Raw = "", Kind = EntryKind.Local, Name = name, Dir = dir,
                    EditMarker = true, Registered = false,
                });
                a.Warnings.Add($"local mod folder '{name}' is not in aLoadOrder - " +
                               "it does not appear on the MODS screen at all");
            }
        }

        var workshopIds = a.Registered.Where(m => m.Kind == EntryKind.Workshop)
            .Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (env.WorkshopContentDir is { } wsRoot)
        {
            foreach (var dir in Directory.EnumerateDirectories(wsRoot))
            {
                var id = Path.GetFileName(dir);
                if (workshopIds.Contains(id)) continue;
                a.UnregisteredWorkshop.Add(new ModEntry
                {
                    Raw = "", Kind = EntryKind.Workshop, Name = id, Dir = dir, Registered = false,
                });
                a.Warnings.Add($"subscribed Workshop item {id} is not in aLoadOrder yet");
            }
        }
    }
}
