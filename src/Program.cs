using System.Reflection;
using System.Runtime.InteropServices;

namespace Ostrasort;

/// <summary>
/// Ostrasort - a LOOT-style load-order analyzer for Ostranauts.
/// Scans core + every local and Workshop-subscribed mod, finds data collisions,
/// suggests a loading_order.json that satisfies the rules, and can generate a
/// merged patch mod for conflicts no order can fix. Read-only by default;
/// --apply / --patch / --unpatch write (with a .bak) and refuse while the game runs.
///
/// Exit codes: 0 = nothing left to do, 2 = actionable suggestions remain, 1 = error.
/// </summary>
public static class Program
{
    public static string Version =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0] ?? "0";

    public static int Main(string[] args)
    {
        var noPause = args.Contains("--no-pause");
        int code;
        try { code = Run(args); }
        catch (Exception e)
        {
            Console.Error.WriteLine($"ostrasort: {e.Message}");
            code = 1;
        }
        PauseIfDoubleClicked(noPause);
        return code;
    }

    private static int Run(string[] args)
    {
        bool apply = false, patch = false, unpatch = false;
        string? gameRoot = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--apply": apply = true; break;
                case "--patch": patch = true; break;
                case "--unpatch": unpatch = true; break;
                case "--no-pause": break;
                case "--game" when i + 1 < args.Length: gameRoot = args[++i]; break;
                case "--version":
                    Console.WriteLine(Version);
                    return 0;
                case "-h" or "--help":
                    Console.WriteLine($"""
                        Ostrasort v{Version} - load-order analyzer & conflict patcher for Ostranauts

                        usage: ostrasort [options]
                          (none)      analyze and report; writes nothing
                          --apply     write the suggested load order (loading_order.json.bak kept)
                          --patch     generate/refresh the "Ostrasort Patch" mod that merges shop
                                      pools two mods both override (conflicts no load order can fix)
                          --unpatch   remove the generated patch mod and its load-order entry
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

        var env = GameEnv.Locate(gameRoot);
        var state = Analyze(env);
        var performed = new List<string>();

        if (unpatch)
        {
            if (!state.Patch.Exists)
                performed.Add("--unpatch: no Ostrasort Patch is installed - nothing to remove.");
            else
            {
                GateGameClosed("--unpatch");
                Patcher.Remove(env);
                performed.Add("Removed the Ostrasort Patch (folder + load-order entry, .bak kept).");
                state = Analyze(env);
            }
        }

        if (patch)
        {
            if (Patcher.PatchableConflicts(state.Analysis).Count == 0)
            {
                performed.Add(state.Patch.Exists
                    ? "--patch: no conflicts left to merge - the installed patch is obsolete; run --unpatch to remove it."
                    : "--patch: no partial-overlap shop-pool conflicts exist - nothing to patch.");
            }
            else
            {
                GateGameClosed("--patch");
                var merged = Patcher.Generate(env, state.Analysis, env.InstalledVersion, Version);
                performed.Add($"Generated the Ostrasort Patch (registered last): {string.Join("; ", merged)}");
                state = Analyze(env);
            }
        }

        if (apply && state.Analysis.OrderChanged)
        {
            GateGameClosed("--apply");
            state.Lo.Write(state.Analysis.SuggestedOrder);
            performed.Add("Applied the suggested load order. Previous file saved as loading_order.json.bak.");
            state = Analyze(env);
        }

        Report.Print(env, state.Scanner, state.Analysis, state.Patch, Version, performed);

        var actionable = state.Analysis.OrderChanged
                      || state.Analysis.HasUnresolvedConflicts
                      || state.Patch.Stale
                      || state.Patch.Obsolete;
        return actionable ? 2 : 0;
    }

    private static void GateGameClosed(string action)
    {
        if (GameEnv.IsGameRunning())
            throw new InvalidOperationException($"Ostranauts is running - close it before {action}. Nothing was written.");
    }

    private sealed record State(LoadOrderFile Lo, Scanner Scanner, Analysis Analysis, PatchInfo Patch);

    /// <summary>One full read-only pass: discover, index, classify, collide, suggest.</summary>
    private static State Analyze(GameEnv env)
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
        var patch = Patcher.Inspect(env, analysis);   // marks collisions the patch resolves
        analysis.BuildSuggestion();
        return new State(lo, scanner, analysis, patch);
    }

    /// <summary>
    /// Finds mods that exist on disk but are missing from aLoadOrder: local
    /// folders under Mods\ (invisible to the in-game MODS screen - the
    /// ConduitToggle incident class) and subscribed Workshop items (the game
    /// re-adds those itself on launch; Ostrasort just surfaces them).
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

    // ------------------------------------------------------- double-click UX ---

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleProcessList(uint[] processList, uint processCount);

    /// <summary>
    /// When launched from Explorer we are the console's only process and the
    /// window vanishes the instant we exit - hold it open so the report is
    /// actually readable. From a terminal (or with --no-pause) this is a no-op.
    /// </summary>
    private static void PauseIfDoubleClicked(bool noPause)
    {
        if (noPause) return;
        try
        {
            if (GetConsoleProcessList(new uint[2], 2) != 1) return;
            Console.WriteLine();
            Console.Write("Press any key to close...");
            Console.ReadKey(intercept: true);
        }
        catch { /* no console at all (redirected) - nothing to hold open */ }
    }
}
