using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Ostrasort;

/// <summary>
/// Ostrasort - load-order analyzer &amp; conflict patcher for Ostranauts.
/// Bare launch (double-click) opens the GUI; flags drive the console paths.
/// Scans core + every local and Workshop-subscribed mod, finds data
/// collisions, suggests a loading_order.json that satisfies the rules, and
/// generates a merged patch mod for conflicts no order can fix - with the
/// player deciding contested items. Writes always keep a .bak and refuse
/// while the game runs.
///
/// Console exit codes: 0 = nothing left to do, 2 = actionable suggestions
/// remain, 1 = error. The GUI always exits 0.
/// </summary>
public static class Program
{
    public static string Version =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0] ?? "0";

    [STAThread]
    public static int Main(string[] args)
    {
        AttachConsoleIfLaunchedFromTerminal(args);   // before any Console access
        try { return Run(args); }
        catch (Exception e)
        {
            Console.Error.WriteLine($"ostrasort: {e.Message}");
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        bool report = false, apply = false, patch = false, unpatch = false, noGui = false, gui = false, smokeGui = false, smokeUndo = false, headless = false, tidy = false, fresh = false, allowRival = false, json = false, profileList = false, merge = false;
        string? gameRoot = null, modsDir = null, installName = null, profileSave = null, profileLoad = null, installZip = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--report": report = true; break;
                case "--headless": headless = true; break;
                case "--json": json = true; break;
                case "--tidy": tidy = true; break;
                case "--smoke-gui": smokeGui = true; break;   // undocumented: construct windows without showing (CI/self-test)
                case "--smoke-undo": smokeUndo = true; break; // undocumented: exercise snapshot undo/redo against a fixture
                case "--dump-collisions": break;              // undocumented: print the collision view as text (handled below)
                case "--normalize": break;                    // undocumented: rewrite loading_order in canonical case (handled below)
                case "--apply": apply = true; break;
                case "--patch": patch = true; break;
                case "--fresh": fresh = true; break;
                case "--profile-list": profileList = true; break;
                case "--profile-save" when i + 1 < args.Length: profileSave = args[++i]; break;
                case "--profile-load" when i + 1 < args.Length: profileLoad = args[++i]; break;
                case "--merge": merge = true; break;
                case "--unpatch": unpatch = true; break;
                case "--install-zip" when i + 1 < args.Length: installZip = args[++i]; break;
                case "--overwrite": break;                              // modifier for --install-zip (handled below)
                case "--allow-rival-stack": allowRival = true; break;   // override the autoloader write-block (at your own risk)
                case "--disable-autoloader": break;                     // park/delete the OstraAutoloader DLL(s) (handled below)
                case "--remove-ffu": break;                             // remove FFU Core (handled below)
                case "--delete": break;                                 // modifier: delete instead of parking as .disabled
                case "--gui": gui = true; break;
                case "--no-gui": noGui = true; break;
                case "--no-pause": break;   // vestigial (WinExe never pauses) - accepted for old scripts
                case "--game" when i + 1 < args.Length: gameRoot = args[++i]; break;
                case "--mods" when i + 1 < args.Length: modsDir = args[++i]; break;
                case "--install" when i + 1 < args.Length: installName = args[++i]; break;
                case "--version":
                    Console.WriteLine(Version);
                    return 0;
                case "-h" or "--help":
                    Console.WriteLine($"""
                        Ostrasort v{Version} - load-order analyzer & conflict patcher for Ostranauts

                        usage: Ostrasort.exe [options]
                          (none)      open the GUI (same as double-clicking the exe)
                          --report    console analysis report; writes nothing
                          --headless  console only, never any window, never waits for a key.
                                      Alone it acts like --report; combine with --apply/--patch/
                                      --unpatch for unattended runs (implies --no-gui)
                          --json      like --headless but the report is one machine-readable
                                      JSON document on stdout (combines with --apply etc.);
                                      exit codes are unchanged (0 clean / 2 actionable / 1 error)
                          --apply     write the suggested load order (loading_order.json.bak kept)
                          --patch     generate/refresh the "Ostrasort Patch" mod that merges shop
                                      pools two mods both override (conflicts no load order can fix);
                                      contested items open the resolver window unless headless
                          --fresh     with --patch: discard all previously stored decisions
                                      (source picks AND exclusions) and rebuild from scratch
                          --unpatch   remove the generated patch mod and its load-order entry
                          --install-zip <p>
                                      install a mod from a .zip package: extract it into the game
                                      (data mods into the Mods folder, BepInEx bundles into the
                                      BepInEx tree, zip-slip safe) and register each data mod in
                                      the load order. A GitHub-style wrapper folder is stripped and
                                      a multi-mod archive installs every mod it holds
                          --overwrite with --install-zip: replace a mod that is already installed
                                      (default: skip it and report)
                          --profile-list          list saved load-order profiles for this install
                          --profile-save <name>   save the current load order as a named profile
                          --profile-load <name>   switch to a saved profile (Replace by default);
                                      mods it doesn't list drop from the order, missing mods are
                                      skipped and reported. Mutually exclusive with --apply
                          --merge     with --profile-load: keep current mods the profile omits,
                                      appended at the end, instead of replacing the whole order
                          --allow-rival-stack
                                      write even while Robyn's OstraAutoloader is installed;
                                      by default writes are refused there because the autoloader
                                      regenerates loading_order.json at every game launch
                                      (FFU itself is supported and never blocks)
                          --disable-autoloader
                                      park the OstraAutoloader DLL(s) as .disabled so Ostrasort
                                      can manage the load order (reversible: rename them back)
                          --remove-ffu
                                      remove FFU Core: FFU MonoMod DLLs and the Minor Fixes Plus
                                      mod are parked as .disabled and unregistered (Ostrasort
                                      recommends Steam Workshop mods only)
                          --delete    with --disable-autoloader / --remove-ffu: delete the files
                                      outright instead of parking them as .disabled

                          --tidy      opt-in cosmetic grouping in the suggestion: core,
                                      infrastructure, code, shells, additive data, overrides, patch
                          --no-gui    never open a window: contested items fall back to the
                                      later-loaded mod's entry (marked for review in the GUI)
                          --game <p>  path to the Ostranauts folder (default: auto-detect via Steam)
                          --mods <p>  path to the mods folder that holds loading_order.json (default:
                                      the game's own strPathMods, else <game>\Ostranauts_Data\Mods)
                          --install <name>
                                      use a saved installation's game + mods folders (add/edit saved
                                      installs in the GUI); --game/--mods still override per slot
                          --version   print the version and exit
                        """);
                    return 0;
                default:
                    Console.Error.WriteLine($"unknown argument '{args[i]}' (try --help)");
                    return 1;
            }
        }
        if (patch && unpatch) { Console.Error.WriteLine("pick one of --patch / --unpatch"); return 1; }
        if (profileLoad is not null && apply) { Console.Error.WriteLine("pick one of --profile-load / --apply (both set the load order)"); return 1; }

        // --install <name> selects a saved installation's directories; an explicit
        // --game / --mods still wins for that slot. An unknown name is a hard error.
        if (installName is not null)
        {
            var inst = InstallationStore.Load().Find(installName)
                ?? throw new InvalidOperationException($"no saved installation named '{installName}' (manage installations in the GUI).");
            gameRoot ??= inst.Game;
            modsDir ??= inst.Mods;
        }

        // --headless / --json: console only, never a window; bare = the report
        if (headless || json)
        {
            noGui = true;
            gui = false;
            if (!apply && !patch && !unpatch) report = true;
        }

        if (smokeGui)
        {
            _ = System.Windows.Application.Current ?? new System.Windows.Application();   // so Fluent theming applies for real
            Gui.ThemeManager.Apply("light");                                             // populate the Application theme resources
            var smokeEnv = GameEnv.Locate(gameRoot, modsDir);
            var smokeState = Engine.Analyze(smokeEnv);
            _ = new Gui.MainWindow(smokeEnv);                                       // ctor runs a full rescan/render
            var smokePlan = Patcher.PlanMerge(smokeEnv, smokeState.Analysis);
            var resolver = new Gui.ResolverDialog(smokePlan);
            _ = new Gui.ParkOrDeleteDialog("self-test", "self-test");
            _ = new Gui.InstallConfirmDialog(new ModInstall.Plan("self-test.zip",
                    new[]
                    {
                        new ModInstall.Component("Self Test Mod", @"C:\Mods\Self Test Mod", "SelfTest/", null,
                            ModInstall.ComponentKind.LocalMod, HasData: true, Exists: true,
                            new[] { @"C:\Mods\Self Test Mod" }),
                    },
                    new[] { "self-test warning" }));
            _ = new Gui.InstallDialog(alreadyInstalled: false);
            _ = new Gui.PromptDialog("self-test");
            _ = new Gui.InstallationsDialog(new InstallationStore(), "self-test");
            _ = new Gui.ProfileSwitchDialog(smokeEnv, smokeState.Analysis,
                    Profile.Capture(smokeState.Analysis, "self-test", smokeEnv.InstalledVersion, null));
            if (smokePlan.ContestedItems.Any() && resolver.SelectorsInTree() == 0)
            {
                Console.Error.WriteLine("gui-smoke FAIL: resolver has contested items but rendered no selectors.");
                return 1;
            }
            // the real install may have no contested plan, so also render a
            // synthetic one that exercises every resolver path (scalar + array
            // diff + union columns, loot rows, the carried/auto expander) - this
            // guards the resolver rendering against a construction-time throw and
            // a zero-selector regression regardless of the local mod set
            var synthResolver = new Gui.ResolverDialog(SyntheticResolverPlan());
            if (synthResolver.SelectorsInTree() == 0)
            {
                Console.Error.WriteLine("gui-smoke FAIL: synthetic contested plan rendered no selectors.");
                return 1;
            }
            // verify Fluent dark theming actually applies (not silently swallowed)
            Gui.ThemeManager.Apply("dark");
#pragma warning disable WPF0001
            var themedDark = System.Windows.Application.Current!.ThemeMode == System.Windows.ThemeMode.Dark;
#pragma warning restore WPF0001
            Gui.ThemeManager.Apply("light");   // leave the shared theme state on light
            if (!themedDark)
            {
                Console.Error.WriteLine("gui-smoke FAIL: dark ThemeMode did not apply.");
                return 1;
            }
            Console.WriteLine($"gui-smoke ok (windows constructed; resolver selectors={resolver.SelectorsInTree()}; " +
                              $"synthetic resolver selectors={synthResolver.SelectorsInTree()}; theming ok)");
            return 0;
        }

        var hardDelete = args.Contains("--delete");

        if (args.Contains("--disable-autoloader"))
        {
            var denv = GameEnv.Locate(gameRoot, modsDir);
            if (GameEnv.IsGameRunning()) { Console.Error.WriteLine("Ostranauts is running - close it first."); return 1; }
            if (FfuContext.Detect(denv) is not { AutoloaderActive: true } dctx)
            {
                Console.WriteLine("No OstraAutoloader plugin found - nothing to disable.");
                return 0;
            }
            var dverb = hardDelete ? "Deleted" : "Disabled";
            foreach (var f in FfuAnalysis.DisableAutoloader(dctx, hardDelete))
            {
                Console.WriteLine($"{dverb}: {f}");
                OpLog.Add($"[cli] {dverb} OstraAutoloader: {f}");
            }
            Console.WriteLine(hardDelete
                ? "OstraAutoloader deleted. Ostrasort now manages the load order - run --report / --apply next."
                : "OstraAutoloader disabled (rename it back to re-enable). Ostrasort now manages the load order - run --report / --apply next.");
            return 0;
        }

        if (args.Contains("--remove-ffu"))
        {
            var renv = GameEnv.Locate(gameRoot, modsDir);
            if (GameEnv.IsGameRunning()) { Console.Error.WriteLine("Ostranauts is running - close it first."); return 1; }
            var rstate = Engine.Analyze(renv);
            var rctx = rstate.Analysis.Ffu ?? new FfuContext();
            var removal = FfuAnalysis.RemoveFfuCore(renv, rctx, rstate.Analysis, hardDelete);
            if (removal.IsEmpty)
            {
                Console.WriteLine("No FFU Core files found (FFU MonoMod DLLs / Minor Fixes Plus) - nothing to remove.");
                return 0;
            }
            var rverb = removal.Deleted ? "Deleted" : "Parked";
            foreach (var f in removal.Affected)
            {
                Console.WriteLine($"{rverb}: {f}");
                OpLog.Add($"[cli] Removed FFU Core ({rverb.ToLowerInvariant()}): {f}");
            }
            foreach (var e in removal.Unregistered)
            {
                Console.WriteLine($"Unregistered: {e}");
                OpLog.Add($"[cli] Removed FFU Core load-order entry: {e}");
            }
            Console.WriteLine(removal.Deleted
                ? "FFU Core removed (files deleted; .bak kept for the load order)."
                : "FFU Core removed (everything renamed to .disabled - rename back to restore; .bak kept for the load order).");
            return 0;
        }

        if (installZip is not null)
        {
            var ienv = GameEnv.Locate(gameRoot, modsDir);
            if (GameEnv.IsGameRunning()) { Console.Error.WriteLine("Ostranauts is running - close it first."); return 1; }

            var plan = ModInstall.Inspect(ienv, installZip);
            foreach (var w in plan.Warnings) Console.WriteLine($"note: {w}");
            if (plan.IsEmpty)
            {
                Console.Error.WriteLine("No installable Ostranauts mod found in the archive.");
                return 1;
            }

            var result = ModInstall.Execute(ienv, plan, null, args.Contains("--overwrite"));
            // registration writes loading_order.json; skip it while the autoloader owns the file
            var canRegister = FfuContext.Detect(ienv) is not { AutoloaderActive: true } || allowRival;
            var registered = canRegister ? ModInstall.RegisterInstalled(ienv, result) : new List<string>();

            foreach (var c in result.Installed)
            {
                Console.WriteLine($"Installed: {c.Name} -> {c.TargetDir}");
                OpLog.Add($"[cli] Installed '{c.Name}' from {Path.GetFileName(installZip)} -> {c.TargetDir}");
            }
            foreach (var r in registered)
            {
                Console.WriteLine($"Registered: {r}");
                OpLog.Add($"[cli] Registered installed mod: {r}");
            }
            foreach (var c in result.Skipped)
                Console.WriteLine($"Skipped (already installed): {c.Name}  - pass --overwrite to replace it");

            if (result.Installed.Count == 0)
            {
                Console.WriteLine("Nothing installed.");
                return result.Skipped.Count > 0 ? 2 : 1;
            }
            if (!canRegister)
                Console.WriteLine("Not registered: OstraAutoloader manages the load order (override: --allow-rival-stack).");
            return 0;
        }

        if (args.Contains("--normalize"))   // undocumented: rewrite loading_order in canonical path case, deduped
        {
            var nenv = GameEnv.Locate(gameRoot, modsDir);
            if (GameEnv.IsGameRunning()) { Console.Error.WriteLine("Ostranauts is running - close it first."); return 1; }
            GateNoRival(FfuContext.Detect(nenv), allowRival, "normalize the load order");
            var nlo = LoadOrderFile.Read(nenv.LoadingOrderPath);
            var before = nlo.Order.ToList();
            nlo.Write(nlo.Order);                        // Write canonicalises case + drops duplicates
            var after = LoadOrderFile.Read(nenv.LoadingOrderPath).Order;
            Console.WriteLine(before.SequenceEqual(after)
                ? "loading_order.json already canonical - no change."
                : $"Normalized loading_order.json: {before.Count} -> {after.Count} entries, canonical path case (.bak kept).");
            return 0;
        }

        if (args.Contains("--dump-collisions"))   // undocumented: print the GUI collision view as text
        {
            var env2 = GameEnv.Locate(gameRoot, modsDir);
            foreach (var v in CollisionView.Build(Engine.Analyze(env2).Analysis))
                Console.WriteLine(new string(' ', v.Indent * 2) + v.Text);
            return 0;
        }

        if (profileList)
        {
            var penv = GameEnv.Locate(gameRoot, modsDir);
            var profiles = ProfileStore.List(penv.LoadingOrderPath);
            if (json)
                Console.WriteLine(JsonReport.ProfilesJson(profiles));
            else if (profiles.Count == 0)
                Console.WriteLine("No saved profiles for this install.");
            else
                foreach (var p in profiles)
                    Console.WriteLine($"{p.Name}  ({p.ModCount} mods" +
                        (p.SavedGameVersion is { Length: > 0 } v ? $", game {v}" : "") +
                        (p.SavedAt is { Length: > 0 } s ? $", saved {s}" : "") + ")");
            return 0;
        }

        if (profileSave is not null)
        {
            var senv = GameEnv.Locate(gameRoot, modsDir);
            var profile = Profile.Capture(Engine.Analyze(senv).Analysis, profileSave, senv.InstalledVersion, DateTime.Now.ToString("o"));
            ProfileStore.Save(senv.LoadingOrderPath, profile);
            OpLog.Add($"[cli] Saved profile '{profileSave}' ({profile.ModCount} mods).");
            Console.WriteLine($"Saved profile '{profileSave}' ({profile.ModCount} mods) for {senv.GameRoot}.");
            return 0;
        }

        if (smokeUndo)
        {
            var env0 = GameEnv.Locate(gameRoot, modsDir);
            var before = UndoOps.Capture(env0, "baseline");
            var state0 = Engine.Analyze(env0);
            var plan0 = Patcher.PlanMerge(env0, state0.Analysis);
            if (plan0.IsEmpty) { Console.Error.WriteLine("smoke-undo needs a fixture with conflicts"); return 1; }
            Patcher.ResolveFallback(plan0);
            Patcher.Generate(env0, plan0, state0.Analysis, env0.InstalledVersion, Version);
            var after = UndoOps.Capture(env0, "patched");

            UndoOps.Restore(env0, before);                       // undo the generate
            var chk1 = UndoOps.Capture(env0, "chk");
            var undoOk = chk1.LoadingOrderText == before.LoadingOrderText && chk1.PatchFiles is null;

            UndoOps.Restore(env0, after);                        // redo it
            var chk2 = UndoOps.Capture(env0, "chk");
            var redoOk = chk2.LoadingOrderText == after.LoadingOrderText
                      && chk2.PatchFiles is not null && after.PatchFiles is not null
                      && chk2.PatchFiles.Count == after.PatchFiles.Count
                      && chk2.PatchFiles.All(kv => after.PatchFiles.TryGetValue(kv.Key, out var b) && kv.Value.SequenceEqual(b));

            UndoOps.Restore(env0, before);                       // leave the fixture clean
            Console.WriteLine($"undo-smoke: undo={(undoOk ? "ok" : "FAIL")} redo={(redoOk ? "ok" : "FAIL")}");
            return undoOk && redoOk ? 0 : 1;
        }

        // bare launch (or explicit --gui) = the app's face
        if (gui || (!report && !apply && !patch && !unpatch && profileLoad is null))
            return RunGui(gameRoot, modsDir, installName);

        var env = GameEnv.Locate(gameRoot, modsDir);
        var state = Engine.Analyze(env, tidy);
        var performed = new List<string>();

        if (apply || patch || unpatch || profileLoad is not null)
            GateNoRival(state.Analysis.Ffu, allowRival, "modify");

        if (profileLoad is not null)
        {
            var profile = ProfileStore.Load(env.LoadingOrderPath, profileLoad)
                ?? throw new InvalidOperationException($"no saved profile named '{profileLoad}' (see --profile-list).");
            GateGameClosed("--profile-load");
            var switchPlan = ProfileSwitch.Plan(env, state.Analysis, profile, merge ? SwitchMode.Merge : SwitchMode.Replace);
            state.Lo.Write(switchPlan.NewOrder);
            performed.Add($"Switched to profile '{profile.Name}' ({(merge ? "merge-append" : "replace")}, {switchPlan.NewOrder.Count} entries).");
            if (switchPlan.Missing.Count > 0)
                performed.Add($"{switchPlan.Missing.Count} profile mod(s) not installed, skipped: " +
                              string.Join(", ", switchPlan.Missing.Select(m => m.DisplayName)) + ".");
            state = Engine.Analyze(env, tidy);
        }

        if (unpatch)
        {
            if (!state.Patch.Exists)
                performed.Add("--unpatch: no Ostrasort Patch is installed - nothing to remove.");
            else
            {
                GateGameClosed("--unpatch");
                Patcher.Remove(env);
                performed.Add("Removed the Ostrasort Patch (folder + load-order entry, .bak kept).");
                state = Engine.Analyze(env, tidy);
            }
        }

        if (patch)
        {
            var plan = Patcher.PlanMerge(env, state.Analysis, fresh);
            if (plan.IsEmpty)
            {
                performed.Add(state.Patch.Exists
                    ? "--patch: no conflicts left to merge - the installed patch is obsolete; run --unpatch to remove it."
                    : "--patch: no mergeable conflicts exist - nothing to patch.");
            }
            else
            {
                GateGameClosed("--patch");
                if (plan.Unresolved.Any())
                {
                    if (noGui)
                    {
                        var n = plan.Unresolved.Count();
                        Patcher.ResolveFallback(plan);
                        performed.Add($"{n} contested item(s) auto-resolved to the later-loaded mod's entry (review them in the GUI).");
                    }
                    else if (!Gui.GuiHost.ShowResolver(plan))
                    {
                        performed.Add("--patch cancelled in the resolver - nothing was written.");
                        Report.Print(env, state.Scanner, state.Analysis, state.Patch, Version, performed);
                        return 2;
                    }
                }
                var result = Patcher.Generate(env, plan, state.Analysis, env.InstalledVersion, Version);
                performed.Add($"Generated the Ostrasort Patch (registered after everything it merges): {string.Join("; ", result.Merged)}");
                foreach (var w in result.SchemaWarnings)
                    performed.Add($"  schema warning (best-effort merge, verify in game): {w}");
                state = Engine.Analyze(env, tidy);
            }
        }

        if (apply && state.Analysis.OrderChanged)
        {
            GateGameClosed("--apply");
            state.Lo.Write(state.Analysis.SuggestedOrder);
            performed.Add("Applied the suggested load order. Previous file saved as loading_order.json.bak.");
            state = Engine.Analyze(env, tidy);
        }

        foreach (var act in performed) OpLog.Add($"[cli] {act}");   // record CLI writes in the shared log
        if (json)
            Console.WriteLine(JsonReport.Build(env, state, Version, performed));   // nothing else on stdout
        else
            Report.Print(env, state.Scanner, state.Analysis, state.Patch, Version, performed);
        return Engine.Actionable(state) ? 2 : 0;
    }

    private static int RunGui(string? gameRoot, string? modsDir, string? installName) =>
        Gui.GuiHost.RunMainWindow(gameRoot, modsDir, installName);

    private static void GateGameClosed(string action)
    {
        if (GameEnv.IsGameRunning())
            throw new InvalidOperationException($"Ostranauts is running - close it before {action}. Nothing was written.");
    }

    /// <summary>
    /// Refuse to write while Robyn's OstraAutoloader is installed: it rewrites
    /// loading_order.json from scratch at EVERY game launch (keeping only
    /// Autoload.Meta.toml-tagged mods), so anything written here would be
    /// undone and unmanaged local mods silently dropped. FFU itself is fine -
    /// Ostrasort applies its ordering rules. Overridable with
    /// --allow-rival-stack for anyone who knows what they're doing.
    /// </summary>
    private static void GateNoRival(FfuContext? ffu, bool allow, string action)
    {
        if (ffu is { AutoloaderActive: true } && !allow)
            throw new InvalidOperationException(
                $"OstraAutoloader detected. It regenerates loading_order.json at every game launch, so Ostrasort won't {action} " +
                "on this install - the write would be undone at the next launch, and local mods without an Autoload.Meta.toml " +
                "(plus |edit/|disabled markers) get dropped by the autoloader anyway. Disable/uninstall OstraAutoloader " +
                "(r2modman: disable it; manual: remove its DLL from BepInEx\\plugins) and let Ostrasort manage the order - " +
                "it understands FFU load groups, dependencies and plugins-dir mods. Override at your own risk with --allow-rival-stack.");
    }

    // ------------------------------------------------------- console plumbing ---

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);
    private const int ATTACH_PARENT_PROCESS = -1;

    /// <summary>
    /// Ostrasort ships as a WinExe (GUI subsystem), so double-clicking it opens
    /// the window with no console flashing up behind it. The console paths still
    /// need to print, so when we were launched from a terminal - and stdout is
    /// <summary>
    /// A hand-built merge plan covering every resolver rendering path, so the
    /// --smoke-gui self-test exercises the dialog even when the real install has
    /// no contested conflict: an object with a contested scalar field, a
    /// contested array field (with the union option), and an auto-merged field;
    /// plus a loot pool with a contested and a carried item.
    /// </summary>
    private static MergePlan SyntheticResolverPlan()
    {
        static ModEntry M(string n) => new() { Raw = n, Kind = EntryKind.Local, Name = n, Dir = n, DisplayName = n };
        static System.Text.Json.Nodes.JsonNode J(string s) => System.Text.Json.Nodes.JsonNode.Parse(s)!;
        var a = M("Alpha Mod");
        var b = M("Beta Mod");

        var descItem = new MergeItem
        {
            Token = "strDesc",
            BaseNode = J("\"A plain sink.\""),
            Options = new()
            {
                new MergeOption(a.Dir!, a.DisplayName!, "\"A fancy sink.\"", J("\"A fancy sink.\"")),
                new MergeOption(b.Dir!, b.DisplayName!, "\"A rusty sink.\"", J("\"A rusty sink.\"")),
            },
        };
        var condsItem = new MergeItem
        {
            Token = "aStartingConds",
            IsArrayField = true,
            BaseNode = J("[\"IsSink=1.0x1\"]"),
            Options = new()
            {
                new MergeOption(a.Dir!, a.DisplayName!, "[IsSink, IsShiny]", J("[\"IsSink=1.0x1\",\"IsShiny=1.0x1\"]")),
                new MergeOption(b.Dir!, b.DisplayName!, "[IsSink, IsRusty]", J("[\"IsSink=1.0x1\",\"IsRusty=1.0x1\"]")),
                new MergeOption("__union__", "union of both", "[IsSink, IsShiny, IsRusty]",
                    J("[\"IsSink=1.0x1\",\"IsShiny=1.0x1\",\"IsRusty=1.0x1\"]")),
            },
        };
        var autoItem = new MergeItem
        {
            Token = "fMass",
            BaseNode = J("1.0"),
            Options = new() { new MergeOption(a.Dir!, a.DisplayName!, "2.0", J("2.0")) },
            ChosenSourceId = a.Dir!,
        };
        var objPlan = new ObjectPlan
        {
            Collision = new Collision { Type = "condowners", ObjName = "AABarTechnoLowPass", Claimants = new() { a, b } },
            Type = "condowners",
            BaseObject = J("{\"strName\":\"AABarTechnoLowPass\"}"),
            Fields = new() { descItem, condsItem, autoItem },
        };

        var lootContested = new MergeItem
        {
            Token = "ItmChair01Loose",
            Options = new()
            {
                new MergeOption(a.Dir!, a.DisplayName!, "ItmChair01Loose=1.0x5"),
                new MergeOption(b.Dir!, b.DisplayName!, "ItmChair01Loose=1.0x9"),
            },
        };
        var lootCarried = new MergeItem
        {
            Token = "ItmLamp01Loose",
            Options = new() { new MergeOption(a.Dir!, a.DisplayName!, "ItmLamp01Loose=1.0x2") },
        };
        var poolPlan = new PoolPlan
        {
            Collision = new Collision { Type = "loot", ObjName = "CONDStationShop", Claimants = new() { a, b } },
            BaseObject = J("{\"strName\":\"CONDStationShop\"}"),
            LootItems = new() { lootContested, lootCarried },
            CoItems = new(),
        };

        return new MergePlan { Pools = new() { poolPlan }, Objects = new() { objPlan } };
    }

    /// not already redirected to a pipe/file (automation, shell redirection) -
    /// attach to that terminal's console and point Console.Out/Error at it.
    /// A pure double-click has no parent console, so AttachConsole simply fails
    /// and we stay silent. Must run before any Console access so the reopened
    /// writers take effect.
    /// </summary>
    private static void AttachConsoleIfLaunchedFromTerminal(string[] args)
    {
        if (args.Length == 0) return;                      // bare launch = GUI only, never needs a console
        if (Console.IsOutputRedirected) return;            // piped/file handles are already valid - don't steal them
        if (!AttachConsole(ATTACH_PARENT_PROCESS)) return; // no parent console (double-clicked a flagged shortcut)

        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
    }
}
