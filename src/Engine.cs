using System.IO;

namespace Ostrasort;

public sealed record EngineState(LoadOrderFile Lo, Scanner Scanner, Analysis Analysis, PatchInfo Patch);

/// <summary>One full read-only pass over the install - shared by the console paths and the GUI.</summary>
public static class Engine
{
    /// <param name="holdNew">
    /// Register mods found on disk but missing from aLoadOrder with the game's own
    /// |disabled marker, so the player ticks them Active when ready. Opt-in: the
    /// GUI's "Add new mods turned off" checkbox and the console's --hold-new.
    /// </param>
    public static EngineState Analyze(GameEnv env, bool tidy = false, IgnoreList? ignore = null,
        FfuOverrideList? ffuOverrides = null, CategoryOverrideList? categoryOverrides = null,
        bool holdNew = false)
    {
        ignore ??= IgnoreList.LoadDefault();
        ffuOverrides ??= FfuOverrideList.LoadDefault();
        categoryOverrides ??= CategoryOverrideList.LoadDefault();
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
        analysis.CoreObjectNames.UnionWith(scanner.CoreIndex.Select(k => k.Name));
        analysis.CoreDataFiles.UnionWith(scanner.CoreDataFiles);
        foreach (var m in analysis.AllMods) scanner.Scan(m);

        if (holdNew) HoldNewMods(analysis);   // after the scan (it needs each mod's class), before anything reads Disabled

        FfuAnalysis.Classify(env, analysis, ffuOverrides);   // FFU block membership, patch targets, FFU hygiene
        CategoryAnalysis.Classify(env, analysis, categoryOverrides);   // game-system category + load-priority tier
        analysis.FindCollisions();
        foreach (var c in analysis.Collisions.Where(c => c.Type == "loot"))
            c.FriendlyName = scanner.LootNames.Describe(c.ObjName);   // readable name for the raw pool id
        FieldDiff.Annotate(env, analysis);
        OverrideAudit.Annotate(env, analysis);   // overrides that undo the last game update, or match the game exactly
        analysis.MarkOrderResolved();   // final-say (late-tier) mods that win by loading last - after FieldDiff so it overrides "mergeable"
        CheckImages(analysis);
        CheckBepInEx(env, analysis);
        CheckCodeMods(env, analysis);
        ModDataCheck.Annotate(env, analysis, scanner);   // findings for mod authors, shown in mod details only
        CheckIgnorePatterns(scanner, analysis);
        WarnAutoloader(analysis);
        var patch = Patcher.Inspect(env, analysis);   // marks collisions the patch resolves
        analysis.BuildSuggestion(tidy);
        return new EngineState(lo, scanner, analysis, patch);
    }

    /// <summary>
    /// "Add new mods turned off": every mod found on disk but missing from
    /// aLoadOrder registers with the game's own |disabled marker instead of
    /// loading. Leaving it out of the file is not an option - the game appends a
    /// subscribed Workshop item itself on the next launch, at the very end, after
    /// the FFU block and the patch - so the entry has to exist for the game to
    /// stop re-adding it, and the marker is what keeps it from loading.
    ///
    /// Marking Disabled here (rather than in the suggestion) means collisions, the
    /// FFU block and the category bands all see the mod the way the file will look
    /// once the suggestion is applied, so a held mod raises no conflicts until it
    /// is ticked Active.
    ///
    /// Two mods are never held: the generated patch (it IS the resolution of a
    /// conflict, not a mod to try later) and the mod loader (holding
    /// infrastructure breaks every code mod rather than deferring one).
    /// </summary>
    private static void HoldNewMods(Analysis a)
    {
        foreach (var m in a.UnregisteredLocal.Where(m => !m.Ignored).Concat(a.UnregisteredWorkshop))
        {
            if (m.IsPatch || m.Class == ModClass.Infrastructure) continue;
            m.HoldOnRegister = true;
            m.Disabled = true;
        }
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
        var mods = a.AllMods.Where(m => m.Dir is not null && !m.IsPatch && m.Loads && m.ImagePaths.Count > 0)
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
        var loaderInstalled = Directory.Exists(Path.Combine(env.CodeBepInExDir, "core"))
                           || File.Exists(Path.Combine(env.GameRoot, "winhttp.dll"));
        var bridgePresent = a.AllMods.Any(m => m.HasPatchers)
                         || Directory.Exists(Path.Combine(env.CodeBepInExDir, "patchers"));
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
        var gamePlugins = Path.Combine(env.CodeBepInExDir, "plugins");
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
    /// Plugins compiled against game code the installed build no longer has.
    /// The player cannot fix the plugin, so the advice is to update it or turn
    /// it off. Names the Workshop item when the plugin is a bridge copy of one.
    /// </summary>
    private static void CheckCodeMods(GameEnv env, Analysis a)
    {
        a.CodeModIssues.AddRange(CodeModCheck.CheckInstall(env));
        foreach (var issue in a.CodeModIssues)
        {
            CodeModOwner(a, issue)?.MissingGameCode.AddRange(issue.Missing);
            var name = CodeModOwnerName(a, issue);
            var sample = string.Join(", ", issue.Missing.Take(3)) + (issue.Missing.Count > 3 ? $", +{issue.Missing.Count - 3} more" : "");
            a.Warnings.Add($"code mod '{name}' ({Path.GetFileName(issue.PluginPath)}) uses game code this version no longer has " +
                           $"({sample}) - it will likely fail or crash the game, so update it or turn it off");
        }
    }

    /// <summary>
    /// The mod that ships a plugin: the Workshop item for a bridge copy, else the
    /// mod whose own plugins folder holds a DLL of that name, else a mod named
    /// like the plugins folder. Null for a plugin no mod accounts for.
    /// </summary>
    public static ModEntry? CodeModOwner(Analysis a, CodeModIssue issue)
    {
        const string ws = "Workshop ";
        if (issue.Owner.StartsWith(ws, StringComparison.Ordinal))
            return a.AllMods.FirstOrDefault(m => m.Kind == EntryKind.Workshop && m.Name == issue.Owner[ws.Length..]);
        var dll = Path.GetFileName(issue.PluginPath);
        return a.AllMods.FirstOrDefault(m => m.Kind != EntryKind.Workshop && m.PluginDlls.Contains(dll))
            ?? a.AllMods.FirstOrDefault(m => m.Kind != EntryKind.Workshop
                && (string.Equals(m.Name, issue.Owner, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(m.DisplayName, issue.Owner, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>The mod's display name for a plugin issue, falling back to its folder.</summary>
    public static string CodeModOwnerName(Analysis a, CodeModIssue issue) =>
        CodeModOwner(a, issue) is { } m ? m.DisplayName ?? m.Name : issue.Owner;

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
        var pluginsRoot = Path.Combine(env.CodeBepInExDir, "plugins");
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
