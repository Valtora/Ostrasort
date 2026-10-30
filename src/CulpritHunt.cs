using System.IO;
using System.Text.Json;

namespace Ostrasort;

/// <summary>
/// A guided search for the mod behind a problem, by halving. Each round turns
/// off about half of the remaining suspects with the game's own |disabled
/// marker; the player launches, says whether the problem still happens, and
/// the suspects shrink to the half that must hold the culprit. Twenty mods take
/// about five launches instead of twenty.
///
/// Mods that depend on a turned-off mod are turned off with it: FFU drops a mod
/// whose requiredMods target is missing, so leaving one on would add a second
/// problem to the test. The mod loader (infrastructure) always stays on, since
/// every code mod needs it. The generated patch stays off for the whole hunt,
/// because it merges mods the hunt turns off.
///
/// The search assumes one culprit. When the problem comes from two mods
/// together, the answers stop making sense, and the hunt says so when it runs
/// out of suspects. State is kept in %APPDATA%\Ostrasort\culprit-hunt.json, so
/// a hunt survives closing Ostrasort while the game runs.
/// </summary>
public sealed class CulpritHunt
{
    public string LoadingOrderPath { get; set; } = "";
    /// <summary>The load order before the hunt, written back when it ends.</summary>
    public List<string> OriginalOrder { get; set; } = new();
    /// <summary>Load-order paths (no marker) of the mods that may still be the culprit.</summary>
    public List<string> Suspects { get; set; } = new();
    /// <summary>Load-order paths turned off in the current round.</summary>
    public List<string> Off { get; set; } = new();
    public int Round { get; set; }
    public string? StartedAt { get; set; }
    /// <summary>Set when every split turns all suspects off together: they cannot be told apart.</summary>
    public bool Inseparable { get; set; }

    public bool Finished => Suspects.Count <= 1 || Inseparable;

    public static string Key(string raw) => raw.Split('|')[0];

    // ---------------------------------------------------------- persistence ---

    public static string DefaultPath => AppPaths.File("culprit-hunt.json");

    /// <summary>The hunt running on this install, or null.</summary>
    public static CulpritHunt? Load(string loadingOrderPath, string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path)) return null;
            var hunt = JsonSerializer.Deserialize<CulpritHunt>(File.ReadAllText(path));
            return hunt is not null && string.Equals(hunt.LoadingOrderPath, loadingOrderPath, StringComparison.OrdinalIgnoreCase)
                ? hunt : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static void Clear(string? path = null)
    {
        path ??= DefaultPath;
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ------------------------------------------------------------- the hunt ---

    /// <summary>Mods that can be the culprit: loaded content, not core, the patch or the mod loader.</summary>
    public static List<ModEntry> Candidates(Analysis a) =>
        a.Registered.Where(m => m.Loads && m.Kind != EntryKind.Core && !m.IsPatch && m.Class != ModClass.Infrastructure).ToList();

    /// <summary>Starts a hunt over every candidate. Null when there are fewer than two.</summary>
    public static CulpritHunt? Start(Analysis a, string loadingOrderPath, DateTime now)
    {
        var suspects = Candidates(a).Select(m => Key(m.Raw)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (suspects.Count < 2) return null;
        return new CulpritHunt
        {
            LoadingOrderPath = loadingOrderPath,
            OriginalOrder = a.Registered.Select(m => m.Raw).ToList(),
            Suspects = suspects,
            StartedAt = now.ToString("o"),
        };
    }

    /// <summary>
    /// Picks this round's mods to turn off and returns the order to write. The
    /// off set is half the suspects plus everything that depends on them; when
    /// that swallows every suspect, the other half or a smaller set is tried.
    /// </summary>
    public List<string> NextRound(Analysis a)
    {
        // what loaded before the hunt: the scan in front of us has this hunt's own
        // turned-off mods marked disabled, and they are still in play
        var activeAtStart = OriginalOrder.Where(r => r.Split('|') is { Length: < 3 } p && !(p.Length == 2 && p[1] == "disabled"))
            .Select(Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var loaded = a.Registered.Where(m => m.Kind != EntryKind.Core && activeAtStart.Contains(Key(m.Raw))).ToList();
        var suspects = loaded.Where(m => Suspects.Contains(Key(m.Raw), StringComparer.OrdinalIgnoreCase)).ToList();
        var half = suspects.Count / 2;
        var candidates = new List<List<ModEntry>>
        {
            suspects.Skip(half).ToList(),   // the later half first: late loaders win conflicts, so they are likelier culprits
            suspects.Take(half).ToList(),
        };
        candidates.AddRange(suspects.Select(s => new List<ModEntry> { s }));

        List<string>? best = null;
        foreach (var start in candidates.Where(c => c.Count > 0))
        {
            var off = Closure(loaded, start);
            var offSuspects = suspects.Count(s => off.Contains(Key(s.Raw)));
            if (offSuspects == 0 || offSuspects == suspects.Count) continue;   // splits nothing
            if (best is null || Math.Abs(offSuspects * 2 - suspects.Count) <
                Math.Abs(suspects.Count(s => best.Contains(Key(s.Raw))) * 2 - suspects.Count))
                best = off.ToList();
            if (start.Count > 1) break;   // a half that splits is good enough
        }

        if (best is null) { Inseparable = true; Off = new(); return OriginalOrder.ToList(); }
        Off = best;
        Round++;
        return OrderFor(Off);
    }

    /// <summary>The original order with the given mods, and the generated patch, turned off.</summary>
    public List<string> OrderFor(IReadOnlyCollection<string> off)
    {
        var offSet = new HashSet<string>(off, StringComparer.OrdinalIgnoreCase);
        return OriginalOrder.Select(raw =>
        {
            var key = Key(raw);
            var isPatch = string.Equals(key, Patcher.FolderName, StringComparison.OrdinalIgnoreCase);
            return (offSet.Contains(key) || isPatch) && !raw.EndsWith("|disabled", StringComparison.Ordinal)
                ? key + "|disabled" : raw;
        }).ToList();
    }

    /// <summary>The player's verdict on the round just played.</summary>
    public void Answer(bool stillBroken)
    {
        var off = new HashSet<string>(Off, StringComparer.OrdinalIgnoreCase);
        // still broken with these off: the culprit is among the mods left on
        Suspects = Suspects.Where(s => off.Contains(s) != stillBroken).ToList();
        Off = new();
    }

    /// <summary>Every loaded mod that must go off with <paramref name="start"/>: those that depend on one, transitively.</summary>
    private static HashSet<string> Closure(List<ModEntry> loaded, List<ModEntry> start)
    {
        var off = new HashSet<string>(start.Select(m => Key(m.Raw)), StringComparer.OrdinalIgnoreCase);
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var m in loaded)
            {
                if (off.Contains(Key(m.Raw)) || m.Class == ModClass.Infrastructure) continue;
                var deps = m.FfuDeps.Select(n => Analysis.ResolveDep(loaded, n)).OfType<ModEntry>();
                if (deps.Any(d => d != m && off.Contains(Key(d.Raw))))
                {
                    off.Add(Key(m.Raw));
                    changed = true;
                }
            }
        }
        return off;
    }

    /// <summary>The order to write when the hunt ends: the original, optionally with the culprit kept off.</summary>
    public List<string> FinalOrder(bool keepCulpritOff) =>
        keepCulpritOff && Suspects.Count == 1 ? OrderFor(Suspects).Select((r, i) =>
            // only the culprit stays off; the patch comes back as it was
            string.Equals(Key(r), Patcher.FolderName, StringComparison.OrdinalIgnoreCase) ? OriginalOrder[i] : r).ToList()
        : OriginalOrder.ToList();
}
