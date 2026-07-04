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
        var noPause = args.Contains("--no-pause") || args.Contains("--headless");
        int code;
        var ranGui = false;
        try { code = Run(args, ref ranGui); }
        catch (Exception e)
        {
            Console.Error.WriteLine($"ostrasort: {e.Message}");
            code = 1;
        }
        if (!ranGui) PauseIfDoubleClicked(noPause);
        return code;
    }

    private static int Run(string[] args, ref bool ranGui)
    {
        bool report = false, apply = false, patch = false, unpatch = false, noGui = false, gui = false, smokeGui = false, smokeUndo = false, headless = false, tidy = false, fresh = false, allowRival = false;
        string? gameRoot = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--report": report = true; break;
                case "--headless": headless = true; break;
                case "--tidy": tidy = true; break;
                case "--smoke-gui": smokeGui = true; break;   // undocumented: construct windows without showing (CI/self-test)
                case "--smoke-undo": smokeUndo = true; break; // undocumented: exercise snapshot undo/redo against a fixture
                case "--dump-collisions": break;              // undocumented: print the collision view as text (handled below)
                case "--normalize": break;                    // undocumented: rewrite loading_order in canonical case (handled below)
                case "--apply": apply = true; break;
                case "--patch": patch = true; break;
                case "--fresh": fresh = true; break;
                case "--unpatch": unpatch = true; break;
                case "--allow-rival-stack": allowRival = true; break;   // override the FFU/Thunderstore write-block (at your own risk)
                case "--gui": gui = true; break;
                case "--no-gui": noGui = true; break;
                case "--no-pause": break;
                case "--game" when i + 1 < args.Length: gameRoot = args[++i]; break;
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
                                      --unpatch for unattended runs (implies --no-gui --no-pause)
                          --apply     write the suggested load order (loading_order.json.bak kept)
                          --patch     generate/refresh the "Ostrasort Patch" mod that merges shop
                                      pools two mods both override (conflicts no load order can fix);
                                      contested items open the resolver window unless headless
                          --fresh     with --patch: discard all previously stored decisions
                                      (source picks AND exclusions) and rebuild from scratch
                          --unpatch   remove the generated patch mod and its load-order entry
                          --allow-rival-stack
                                      proceed on an FFU/Thunderstore (OstraAutoloader) install;
                                      by default every write is refused there

                          --tidy      opt-in cosmetic grouping in the suggestion: core,
                                      infrastructure, code, shells, additive data, overrides, patch
                          --no-gui    never open a window: contested items fall back to the
                                      later-loaded mod's entry (marked for review in the GUI)
                          --game <p>  path to the Ostranauts folder (default: auto-detect via Steam)
                          --no-pause  do not wait for a key press when launched by double-click
                          --version   print the version and exit
                        """);
                    return 0;
                default:
                    Console.Error.WriteLine($"unknown argument '{args[i]}' (try --help)");
                    return 1;
            }
        }
        if (patch && unpatch) { Console.Error.WriteLine("pick one of --patch / --unpatch"); return 1; }

        // --headless: console only, never a window; bare headless = the report
        if (headless)
        {
            noGui = true;
            gui = false;
            if (!apply && !patch && !unpatch) report = true;
        }

        if (smokeGui)
        {
            var smokeEnv = GameEnv.Locate(gameRoot);
            var smokeState = Engine.Analyze(smokeEnv);
            _ = new Gui.MainWindow(smokeEnv);                                       // ctor runs a full rescan/render
            var smokePlan = Patcher.PlanMerge(smokeEnv, smokeState.Analysis);
            var resolver = new Gui.ResolverDialog(smokePlan);
            _ = new Gui.RivalStackDialog(new RivalStack(true, true, true, new[] { "self-test evidence" }));
            if (smokePlan.ContestedItems.Any() && resolver.SelectorsInTree() == 0)
            {
                Console.Error.WriteLine("gui-smoke FAIL: resolver has contested items but rendered no selectors.");
                return 1;
            }
            Console.WriteLine($"gui-smoke ok (windows constructed; resolver selectors={resolver.SelectorsInTree()})");
            return 0;
        }

        if (args.Contains("--normalize"))   // undocumented: rewrite loading_order in canonical path case, deduped
        {
            var nenv = GameEnv.Locate(gameRoot);
            if (GameEnv.IsGameRunning()) { Console.Error.WriteLine("Ostranauts is running - close it first."); return 1; }
            GateNoRival(RivalStack.Detect(nenv), allowRival, "normalize the load order");
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
            var env2 = GameEnv.Locate(gameRoot);
            foreach (var v in CollisionView.Build(Engine.Analyze(env2).Analysis))
                Console.WriteLine(new string(' ', v.Indent * 2) + v.Text);
            return 0;
        }

        if (smokeUndo)
        {
            var env0 = GameEnv.Locate(gameRoot);
            var before = UndoOps.Capture(env0, "baseline");
            var plan0 = Patcher.PlanMerge(env0, Engine.Analyze(env0).Analysis);
            if (plan0.IsEmpty) { Console.Error.WriteLine("smoke-undo needs a fixture with conflicts"); return 1; }
            Patcher.ResolveFallback(plan0);
            Patcher.Generate(env0, plan0, env0.InstalledVersion, Version);
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
        if (gui || (!report && !apply && !patch && !unpatch))
        {
            ranGui = true;
            return RunGui(gameRoot);
        }

        var env = GameEnv.Locate(gameRoot);
        var state = Engine.Analyze(env, tidy);
        var performed = new List<string>();

        if (apply || patch || unpatch)
            GateNoRival(state.Analysis.Rival, allowRival, "modify");

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
                var result = Patcher.Generate(env, plan, env.InstalledVersion, Version);
                performed.Add($"Generated the Ostrasort Patch (registered last): {string.Join("; ", result.Merged)}");
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
        Report.Print(env, state.Scanner, state.Analysis, state.Patch, Version, performed);
        return Engine.Actionable(state) ? 2 : 0;
    }

    private static int RunGui(string? gameRoot)
    {
        HideOwnConsole();
        return Gui.GuiHost.RunMainWindow(gameRoot);
    }

    private static void GateGameClosed(string action)
    {
        if (GameEnv.IsGameRunning())
            throw new InvalidOperationException($"Ostranauts is running - close it before {action}. Nothing was written.");
    }

    /// <summary>
    /// Refuse to write on an install that runs a rival, non-Workshop load-order
    /// manager (FFU / Robyn's OstraAutoloader) - they generate loading_order.json
    /// themselves, so writing here would fight them. Overridable with
    /// --allow-rival-stack for anyone who knows what they're doing.
    /// </summary>
    private static void GateNoRival(RivalStack? rival, bool allow, string action)
    {
        if (rival is { } r && !allow)
            throw new InvalidOperationException(
                $"FFU / Thunderstore stack detected ({r.Summary}). Ostrasort is Steam-Workshop-only and won't {action} " +
                "on this install - Robyn's OstraAutoloader manages loading_order.json itself, so the two fight and will " +
                "likely break your FFU setup. Use one or the other, not both. Override at your own risk with --allow-rival-stack.");
    }

    // ------------------------------------------------------- console plumbing ---

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleProcessList(uint[] processList, uint processCount);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private static bool OwnsConsoleAlone()
    {
        try { return GetConsoleProcessList(new uint[2], 2) == 1; }
        catch { return false; }
    }

    /// <summary>Double-clicked into GUI mode: hide the console window we spawned - it is just noise.</summary>
    private static void HideOwnConsole()
    {
        if (OwnsConsoleAlone()) ShowWindow(GetConsoleWindow(), 0 /* SW_HIDE */);
    }

    /// <summary>
    /// When a console path was launched from Explorer we are the console's only
    /// process and the window vanishes the instant we exit - hold it open so
    /// the report is readable. From a terminal (or with --no-pause) it's a no-op.
    /// </summary>
    private static void PauseIfDoubleClicked(bool noPause)
    {
        if (noPause) return;
        try
        {
            if (!OwnsConsoleAlone()) return;
            Console.WriteLine();
            Console.Write("Press any key to close...");
            Console.ReadKey(intercept: true);
        }
        catch { /* no console at all (redirected) - nothing to hold open */ }
    }
}
