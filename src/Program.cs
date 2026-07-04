namespace Ostrasort;

/// <summary>
/// Ostrasort - a LOOT-style load-order analyzer for Ostranauts.
/// Scans core + every local and Workshop-subscribed mod, finds data collisions,
/// and suggests a loading_order.json that satisfies the rules. Read-only by
/// default; --apply writes (with a .bak) and refuses while the game runs.
///
/// Exit codes: 0 = order already satisfies every rule (or applied OK),
///             2 = changes suggested (analysis mode), 1 = error.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        var apply = false;
        string? gameRoot = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--apply": apply = true; break;
                case "--game" when i + 1 < args.Length: gameRoot = args[++i]; break;
                case "-h" or "--help":
                    Console.WriteLine("usage: ostrasort [--apply] [--game <path-to-Ostranauts-install>]");
                    return 0;
                default:
                    Console.Error.WriteLine($"unknown argument '{args[i]}' (try --help)");
                    return 1;
            }
        }

        try
        {
            var env = GameEnv.Locate(gameRoot);
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
            analysis.BuildSuggestion();

            var applied = false;
            if (apply && analysis.OrderChanged)
            {
                if (GameEnv.IsGameRunning())
                {
                    Report.Print(env, scanner, analysis, applied: false);
                    Console.Error.WriteLine("\nOstranauts is running - close it before --apply. Nothing was written.");
                    return 1;
                }
                lo.Write(analysis.SuggestedOrder);
                applied = true;
            }

            Report.Print(env, scanner, analysis, applied);
            return applied || !analysis.OrderChanged ? 0 : 2;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"ostrasort: {e.Message}");
            return 1;
        }
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
}
