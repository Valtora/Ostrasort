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
        LoadOrderFile lo;
        try { lo = LoadOrderFile.Read(env.LoadingOrderPath); }
        catch (FileNotFoundException) when (!env.ModsDirFromStrPathMods && StrPathModsText(env) is { } stale)
        {
            // the game reads a strPathMods folder that is gone, so it never writes a
            // list here, and "launch the game once" would not help
            throw new FileNotFoundException($"loading_order.json not found at '{env.LoadingOrderPath}'. {stale}", env.LoadingOrderPath);
        }
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
        CheckInstall(env, scanner, analysis);
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

    /// <summary>
    /// Problems with the install itself, listed ahead of every other warning
    /// because they decide whether the rest can be trusted: a game folder that is
    /// not a complete install (so nothing was compared with the base game), and a
    /// strPathMods that names another Ostranauts folder's Mods folder. Both are the
    /// player's to fix, and the text says how.
    /// </summary>
    private static void CheckInstall(GameEnv env, Scanner scanner, Analysis a)
    {
        var first = new List<string>();
        if (!env.IsCompleteInstall)
            first.Add(a.IncompleteInstallWarning = IncompleteInstallText(env, scanner.CoreIndex.Count));
        if (env.StrPathModsOtherRoot is not null && StrPathModsText(env) is { } stale)
            first.Add(a.StrPathModsWarning = stale);
        a.Warnings.InsertRange(0, first);
    }

    private static string MissingPartsPhrase(GameEnv env)
    {
        var parts = env.MissingInstallParts;
        return parts.Count == 1 ? $"{parts[0]} is missing" : $"{string.Join(" and ", parts)} are missing";
    }

    /// <summary>The incomplete-install warning, naming the missing parts and the size of the core index.</summary>
    public static string IncompleteInstallText(GameEnv env, int coreObjects)
    {
        return $"{env.GameRoot} is not a complete Ostranauts install ({MissingPartsPhrase(env)}). " +
               $"Ostrasort read {coreObjects:N0} base game objects from it, so it has nothing to compare your mods with, " +
               "and the conflict, merge and game update checks are not reliable. " +
               "Use Manage next to Installation to add the folder the game runs from " +
               "(in Steam, right click Ostranauts and choose Manage, then Browse local files), " +
               "or pass it with --game from a terminal.";
    }

    /// <summary>
    /// Why the fixes are off on this install, or null when they are allowed. On a
    /// folder that is not a complete install Ostrasort only reports: the
    /// compatibility patch merges every mod against the base game, which is not
    /// there, and the suggested order is built from that folder too (its tidy
    /// grouping tells overriding mods from additive ones by the base game, and it
    /// registers Workshop items from the library beside the folder, which is not
    /// the one the game runs from). Removing an existing patch, applying an order
    /// you arranged yourself and switching profiles stay allowed. The GUI shows
    /// this on the disabled buttons; the CLI refuses with <see cref="RefuseIncompleteInstall"/>.
    /// </summary>
    public static string? FixesBlockedReason(GameEnv env) => env.IsCompleteInstall ? null :
        "Turned off because this game folder is not a complete Ostranauts install, so there is no base game " +
        "to merge mods against. The first warning on the Warnings tab says how to pick the folder the game runs from.";

    /// <summary>
    /// The CLI's refusal to <paramref name="action"/> (a verb phrase, such as "build
    /// the compatibility patch") on an incomplete install. Throws before anything
    /// is written; does nothing on a complete install.
    /// </summary>
    public static void RefuseIncompleteInstall(GameEnv env, string action)
    {
        if (env.IsCompleteInstall) return;
        throw new InvalidOperationException(
            $"{env.GameRoot} is not a complete Ostranauts install ({MissingPartsPhrase(env)}), so Ostrasort will not " +
            $"{action} there. Without the base game, and without the folder the game runs from, its merges and " +
            "suggestions cannot be trusted. Pick that folder with --game, or with Manage next to Installation in the " +
            "window, then run the command again. Reports still run, and nothing was written.");
    }

    /// <summary>
    /// The stale-strPathMods warning, or null when strPathMods names no other
    /// install. Two cases: the other folder exists (ModsDir follows it, as the game
    /// does, so mods still load), or it is gone (the game loads no mods at all).
    /// </summary>
    public static string? StrPathModsText(GameEnv env)
    {
        if (env.StrPathModsOtherRoot is not { } other || env.StrPathMods is not { } raw) return null;
        var ownMods = Path.Combine(env.GameRoot, "Ostranauts_Data", "Mods");
        var otherMods = Path.Combine(other, "Ostranauts_Data", "Mods");
        var value = env.GameRoot.Replace('\\', '/') + "/Ostranauts_Data/Mods/loading_order.json";
        const string why = "This is usually left over from an old install, because the game keeps strPathMods " +
                           "in its settings.json through every reinstall. ";
        const string where = " The Files tab of the game's Options shows the current path, and its Settings button " +
                             "opens the folder that holds settings.json.";
        var set = $"set strPathMods in settings.json to {value} (forward slashes, exactly as shown), " +
                  "then start the game again and reopen Ostrasort.";
        return env.ModsDirFromStrPathMods
            ? $"The game reads its mod list from {raw}, which is the Mods folder of a different Ostranauts folder " +
              $"({other}), not of the install Ostrasort opened ({env.GameRoot}). " + why +
              "Ostrasort reads and writes the same folder as the game, so your mods still load. " +
              $"To go back to this install's own Mods folder, close the game, copy loading_order.json and any mod " +
              $"folders you added by hand from {otherMods} to {ownMods}, " + set + where
            : $"The game reads its mod list from {raw}, the Mods folder of a different Ostranauts folder ({other}), " +
              "and that folder does not exist, so the game loads no mods at all. " +
              $"Ostrasort opens this install's own Mods folder ({ownMods}) instead, which the game is not reading. " + why +
              "To fix it, close the game, " + set + where;
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
        // a folder that is not a real install has no BepInEx either, and telling
        // the player to install it again would be wrong; the incomplete-install
        // warning (CheckInstall) says what to fix instead
        var realFolder = env.IsCompleteInstall;
        if (realFolder && modsWithPlugins.Count > 0 && !loaderInstalled)
            a.Warnings.Add($"{modsWithPlugins.Count} mod(s) ship BepInEx plugins but BepInEx is NOT installed in the game folder " +
                           "- none of their code will load (subscribe to the BepInEx Mod Loader and finish its setup)");
        else if (realFolder && modsWithPlugins.Any(m => m.Kind == EntryKind.Workshop) && !bridgePresent)
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
