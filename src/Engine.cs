using System.IO;

namespace Ostrasort;

public sealed record EngineState(LoadOrderFile Lo, Scanner Scanner, Analysis Analysis, PatchInfo Patch);

/// <summary>One full read-only pass over the install - shared by the console paths and the GUI.</summary>
public static class Engine
{
    public static EngineState Analyze(GameEnv env, bool tidy = false, IgnoreList? ignore = null)
    {
        ignore ??= IgnoreList.LoadDefault();
        var lo = LoadOrderFile.Read(env.LoadingOrderPath);
        var analysis = new Analysis
        {
            Registered = lo.Order.Select(raw => ModEntry.Parse(raw, env)).ToList(),
            Ffu = FfuContext.Detect(env),
            IgnorePatterns = lo.IgnorePatterns,
        };
        DiscoverUnregistered(env, analysis, ignore);

        var scanner = new Scanner(env, lo.IgnorePatterns);
        scanner.IndexCore();
        foreach (var m in analysis.AllMods) scanner.Scan(m);

        FfuAnalysis.Classify(env, analysis);          // FFU block membership, patch targets, FFU hygiene
        analysis.FindCollisions();
        foreach (var c in analysis.Collisions.Where(c => c.Type == "loot"))
            c.FriendlyName = scanner.LootNames.Describe(c.ObjName);   // readable name for the raw pool id
        FieldDiff.Annotate(env, analysis);
        CheckImages(analysis);
        CheckBepInEx(env, analysis);
        CheckIgnorePatterns(scanner, analysis);
        WarnAutoloader(analysis);
        var patch = Patcher.Inspect(env, analysis);   // marks collisions the patch resolves
        analysis.BuildSuggestion(tidy);
        return new EngineState(lo, scanner, analysis, patch);
    }

    /// <summary>
    /// Surfaces what loading_order.json's aIgnorePatterns silently remove: the
    /// game skips every data file (core or any mod's) whose path contains a
    /// pattern, so objects in those files simply never load.
    /// </summary>
    private static void CheckIgnorePatterns(Scanner scanner, Analysis a)
    {
        if (a.IgnorePatterns.Count == 0) return;

        static string Sample(IReadOnlyList<(string File, string Pattern)> hits) =>
            string.Join(", ", hits.Take(3).Select(h => $"{h.File} ('{h.Pattern}')")) +
            (hits.Count > 3 ? $", +{hits.Count - 3} more" : "");

        if (scanner.IgnoredCoreFiles.Count > 0)
            a.Warnings.Add($"aIgnorePatterns skips {scanner.IgnoredCoreFiles.Count} CORE data file(s) - " +
                           $"their objects never load: {Sample(scanner.IgnoredCoreFiles)}");
        foreach (var m in a.AllMods.Where(m => m.IgnoredFiles.Count > 0))
            a.Warnings.Add($"aIgnorePatterns skips {m.IgnoredFiles.Count} file(s) of '{m.DisplayName ?? m.Name}' - " +
                           $"their objects never load: {Sample(m.IgnoredFiles)}");
    }

    /// <summary>Two mods shipping the same relative image path - last loaded wins the whole file.</summary>
    private static void CheckImages(Analysis a)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // a duplicated entry must not self-collide
        var mods = a.AllMods.Where(m => m.Dir is not null && !m.IsPatch && !m.Disabled && m.ImagePaths.Count > 0)
            .Where(m => seen.Add(Analysis.IdentityOf(m)))
            .ToList();
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

    /// <summary>
    /// FFU itself is supported; the one true rival is Robyn's OstraAutoloader
    /// plugin, which rewrites loading_order.json from scratch at every game
    /// launch - anything Ostrasort writes would be undone, so writes are gated
    /// while it is active.
    /// </summary>
    private static void WarnAutoloader(Analysis a)
    {
        if (a.Ffu is { AutoloaderActive: true })
            a.Warnings.Add("OstraAutoloader is active - it regenerates loading_order.json at every game launch, " +
                           "so Ostrasort is read-only on this install. Disable/uninstall the autoloader to let " +
                           "Ostrasort manage the order (it understands FFU load groups and plugins-dir mods).");
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
    private static void DiscoverUnregistered(GameEnv env, Analysis a, IgnoreList ignore)
    {
        var localNames = a.Registered.Where(m => m.Kind == EntryKind.Local)
            .Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(env.ModsDir))
        {
            foreach (var dir in Directory.EnumerateDirectories(env.ModsDir))
            {
                var name = Path.GetFileName(dir);
                if (name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)) continue;   // parked by Remove FFU
                if (name.EndsWith(".ostrasort-tmp", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".ostrasort-old", StringComparison.OrdinalIgnoreCase)) continue;   // install staging leftovers
                if (localNames.Contains(name)) continue;
                if (!File.Exists(Path.Combine(dir, "mod_info.json"))) continue;   // not a mod folder
                var entry = new ModEntry
                {
                    Raw = "", Kind = EntryKind.Local, Name = name, Dir = dir,
                    EditMarker = true, Registered = false,
                };
                entry.Ignored = ignore.Contains(IgnoreList.KeyFor(env, entry));
                a.UnregisteredLocal.Add(entry);
                if (!entry.Ignored)
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

        // FFU/Thunderstore data mods living under BepInEx\plugins (mod_info.json
        // + data\) load like any other mod but must be registered by absolute
        // path - the OstraAutoloader normally does that; without it, Ostrasort does.
        var pluginsRoot = Path.Combine(env.BepInExDir, "plugins");
        if (Directory.Exists(pluginsRoot))
        {
            List<string> infoFiles;
            try { infoFiles = Directory.EnumerateFiles(pluginsRoot, "mod_info.json", SearchOption.AllDirectories).ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                a.Warnings.Add($"could not scan BepInEx\\plugins for data mods: {e.Message}");
                infoFiles = new();
            }
            // GetFullPath normalises separators, so an entry written with forward
            // slashes still matches the enumerated on-disk folder
            var registeredDirs = a.Registered.Where(m => m.Kind == EntryKind.PluginDir && m.Dir is not null)
                .Select(m => Path.TrimEndingDirectorySeparator(Path.GetFullPath(m.Dir!)))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var info in infoFiles)
            {
                var dir = Path.GetDirectoryName(info)!;
                var rel = Path.GetRelativePath(pluginsRoot, dir);
                // parked by Remove FFU: match the ".disabled" suffix on a PATH
                // SEGMENT under the plugins root, not anywhere in the absolute
                // path (an install path merely containing that substring would
                // otherwise hide every plugins-dir mod)
                if (rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                       .Any(seg => seg.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))) continue;
                if (rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0]
                       .Equals("Workshop", StringComparison.OrdinalIgnoreCase)) continue;   // bridge-managed copies
                if (!Directory.Exists(Path.Combine(dir, "data"))) continue;                 // no data payload to load
                if (registeredDirs.Contains(Path.TrimEndingDirectorySeparator(dir))) continue;
                var entry = new ModEntry
                {
                    Raw = "", Kind = EntryKind.PluginDir, Name = Path.GetFileName(dir), Dir = dir, Registered = false,
                };
                entry.Ignored = ignore.Contains(IgnoreList.KeyFor(env, entry));
                a.UnregisteredLocal.Add(entry);
                if (!entry.Ignored)
                    a.Warnings.Add($"data mod '{Path.GetFileName(dir)}' under BepInEx\\plugins is not in aLoadOrder - " +
                                   "the game loads only what aLoadOrder lists");
            }
        }
    }
}
