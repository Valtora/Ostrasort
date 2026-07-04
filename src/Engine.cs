using System.IO;

namespace Ostrasort;

public sealed record EngineState(LoadOrderFile Lo, Scanner Scanner, Analysis Analysis, PatchInfo Patch);

/// <summary>One full read-only pass over the install - shared by the console paths and the GUI.</summary>
public static class Engine
{
    public static EngineState Analyze(GameEnv env)
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
        return new EngineState(lo, scanner, analysis, patch);
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
