using System.IO;
using System.Text.RegularExpressions;

namespace Ostrasort;

/// <summary>
/// Best-effort correlation of the game's own logs with the current mod set.
/// After a launch the game overwrites Player.log (BepInEx overwrites its own
/// log), so the whole file is the last session. We scan it for load-time
/// error/warning lines and attribute each to the mod responsible, then surface
/// them as warnings + per-mod notes on the next rescan - closing the loop
/// between "Ostrasort says this is fine" and "the game actually loaded it".
///
/// Attribution is deliberately conservative. Code mods are attributed cleanly
/// by the BepInEx source tag ([Error :&lt;plugin name&gt;]). Data-mod errors have
/// no per-folder marker in the log, so they are attributed only when a JSON
/// filename (or a claimed strName) in the line matches exactly one installed
/// mod; ambiguous or unrecognised lines are reported as an un-attributed
/// summary rather than guessed at.
/// </summary>
public static class LogCorrelation
{
    /// <summary>One attributed (or un-attributable) log issue. <see cref="Candidates"/> holds guesses when attribution was ambiguous.</summary>
    public sealed record LogIssue(string Severity, string Line, ModEntry? Mod, IReadOnlyList<ModEntry> Candidates);

    // BepInEx line tag: "[Error  :Ship's Water]" / "[Warning:   BepInEx]"
    private static readonly Regex BepTag = new(@"^\[(Warning|Error|Fatal)\s*:\s*([^\]]+?)\s*\]", RegexOptions.Compiled);
    // the game's own severity marker: "#Error#" / "#Warning#"
    private static readonly Regex GameTag = new(@"#(Error|Warning)#", RegexOptions.Compiled);
    private static readonly Regex JsonToken = new(@"[\w\-./\\]+\.json", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Word = new(@"[A-Za-z0-9_]{4,}", RegexOptions.Compiled);
    private static readonly Regex LoadingJson = new(@"Loading json:\s*([\w\-./\\]+\.json)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // BepInEx / engine sources that are the framework itself, not a mod
    private static readonly HashSet<string> Framework =
        new(new[] { "BepInEx", "Unity", "Mono", "HarmonyX", "Harmony", "Doorstop", "Preloader" }, StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads each existing log file and records attributed issues as warnings + per-mod notes.</summary>
    public static void Annotate(Analysis a, params string[] logPaths)
    {
        // each log is scanned separately so a "Loading json:" context from one
        // file can never leak into the next one's lines
        var files = new List<IReadOnlyList<string>>();
        foreach (var path in logPaths)
        {
            try
            {
                if (File.Exists(path)) files.Add(File.ReadLines(path).ToList());
            }
            catch { /* an unreadable log is not fatal - just no correlation */ }
        }
        if (files.Count == 0) return;

        var issues = Collect(a, files);
        if (issues.Count == 0) return;

        // attributed: one grouped note per responsible mod
        foreach (var g in issues.Where(i => i.Mod is not null).GroupBy(i => i.Mod!))
        {
            var errs = g.Count(i => i.Severity is "Error" or "Fatal");
            var note = $"logged {Count(errs, g.Count() - errs)} at the last game launch: “{Truncate(g.First().Line)}”";
            a.Warnings.Add($"'{Name(g.Key)}' {note}");
            g.Key.LogNotes.Add(note);
        }

        // everything we could not pin to one mod: a single honest summary
        var rest = issues.Where(i => i.Mod is null).ToList();
        if (rest.Count > 0)
        {
            var errs = rest.Count(i => i.Severity is "Error" or "Fatal");
            var sample = rest[0];
            var cand = sample.Candidates.Count > 0
                ? $" (maybe {string.Join(", ", sample.Candidates.Select(Name))})" : "";
            a.Warnings.Add($"the game log shows {Count(errs, rest.Count - errs)} load issue(s) at the last launch " +
                           $"not tied to a specific mod: “{Truncate(sample.Line)}”{cand} (see the Logs tab)");
        }
    }

    /// <summary>Parses raw log lines into attributed issues (no I/O - the testable core).</summary>
    public static List<LogIssue> Collect(Analysis a, IEnumerable<string> lines) =>
        Collect(a, new[] { (IReadOnlyList<string>)lines.ToList() });

    /// <summary>Multi-file overload: one shared dedupe set, but per-file loading context.</summary>
    public static List<LogIssue> Collect(Analysis a, IEnumerable<IReadOnlyList<string>> files)
    {
        var byName = BuildNameIndex(a);
        var (byPath, byBase) = BuildFileIndex(a);
        var byStrName = BuildStrNameIndex(a);

        var issues = new List<LogIssue>();
        var seen = new HashSet<string>(StringComparer.Ordinal);   // dedupe identical repeated lines
        foreach (var file in files)
            CollectFile(file, byName, byPath, byBase, byStrName, issues, seen);
        return issues;
    }

    private static void CollectFile(IReadOnlyList<string> lines,
        Dictionary<string, ModEntry> byName,
        Dictionary<string, List<ModEntry>> byPath, Dictionary<string, List<ModEntry>> byBase,
        Dictionary<string, List<ModEntry>> byStrName,
        List<LogIssue> issues, HashSet<string> seen)
    {
        string? loadingContext = null;

        foreach (var raw in lines)
        {
            // cheap pre-filter: track the file being loaded, skip non-issue lines fast
            var lj = LoadingJson.Match(raw);
            if (lj.Success) { loadingContext = lj.Groups[1].Value; continue; }
            if (!LooksLikeIssue(raw)) { loadingContext = null; continue; }
            // ^ any ordinary line ends the "just loaded this file" window: the
            // context may carry across a load error's immediate follow-up lines,
            // but never to an unrelated exception logged later in the session

            var line = raw.Trim();

            var bep = BepTag.Match(line);
            if (bep.Success)
            {
                var source = bep.Groups[2].Value;
                if (Framework.Contains(source)) continue;   // the loader itself, not a mod - too noisy to surface
                if (!seen.Add(line)) continue;
                var mod = byName.GetValueOrDefault(Norm(source));
                issues.Add(new LogIssue(bep.Groups[1].Value, line, mod, mod is null ? Array.Empty<ModEntry>() : new[] { mod }));
                continue;
            }

            // the game's own data-load line
            var sev = GameTag.Match(line) is { Success: true } g ? g.Groups[1].Value
                    : line.Contains("Exception", StringComparison.Ordinal) ? "Error"
                    : null;
            if (sev is null) continue;
            if (!seen.Add(line)) continue;

            var owners = AttributeData(line, loadingContext, byPath, byBase, byStrName);
            var mod2 = owners.Count == 1 ? owners[0] : null;
            issues.Add(new LogIssue(sev, line, mod2, owners));
        }
    }

    // ------------------------------------------------------------- indexes ---

    // NB: the generated patch is indexed like any other mod. Excluding it made
    // its own load errors structurally unattributable - and worse, when exactly
    // one SOURCE mod shipped a same-named file, the patch's error was uniquely
    // (and wrongly) pinned on that innocent mod. Including it either attributes
    // the patch cleanly or honestly widens the candidate list.
    private static Dictionary<string, ModEntry> BuildNameIndex(Analysis a)
    {
        var map = new Dictionary<string, ModEntry>(StringComparer.Ordinal);
        foreach (var m in a.AllMods)
        {
            if (m.DisplayName is { Length: > 0 } d) map.TryAdd(Norm(d), m);
            map.TryAdd(Norm(m.Name), m);
        }
        return map;
    }

    private static (Dictionary<string, List<ModEntry>> ByPath, Dictionary<string, List<ModEntry>> ByBase) BuildFileIndex(Analysis a)
    {
        var byPath = new Dictionary<string, List<ModEntry>>(StringComparer.OrdinalIgnoreCase);
        var byBase = new Dictionary<string, List<ModEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in a.AllMods)
            foreach (var f in m.DataFiles)
            {
                Add(byPath, f, m);
                var slash = f.LastIndexOf('/');
                Add(byBase, slash >= 0 ? f[(slash + 1)..] : f, m);
            }
        return (byPath, byBase);
    }

    private static Dictionary<string, List<ModEntry>> BuildStrNameIndex(Analysis a)
    {
        var map = new Dictionary<string, List<ModEntry>>(StringComparer.Ordinal);
        foreach (var m in a.AllMods)
            foreach (var (_, name) in m.Claims.Keys)
                if (name.Length >= 4) Add(map, name, m);
        return map;
    }

    private static void Add(Dictionary<string, List<ModEntry>> map, string key, ModEntry m)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = new();
        if (!list.Contains(m)) list.Add(m);
    }

    // --------------------------------------------------------- attribution ---

    /// <summary>The mod(s) an un-tagged data line points at: by JSON filename, the loading context, then a claimed strName.</summary>
    private static IReadOnlyList<ModEntry> AttributeData(string line, string? context,
        Dictionary<string, List<ModEntry>> byPath, Dictionary<string, List<ModEntry>> byBase,
        Dictionary<string, List<ModEntry>> byStrName)
    {
        foreach (Match m in JsonToken.Matches(line))
            if (FileOwners(m.Value, byPath, byBase) is { Count: > 0 } hit) return hit;

        if (context is not null && FileOwners(context, byPath, byBase) is { Count: > 0 } ctx) return ctx;

        // last resort: a claimed strName appearing as a whole word in the line
        var named = new List<ModEntry>();
        foreach (Match w in Word.Matches(line))
            if (byStrName.TryGetValue(w.Value, out var owners))
                foreach (var o in owners)
                    if (!named.Contains(o)) named.Add(o);
        return named;
    }

    private static List<ModEntry>? FileOwners(string token, Dictionary<string, List<ModEntry>> byPath, Dictionary<string, List<ModEntry>> byBase)
    {
        var norm = token.Replace('\\', '/');
        // most specific first: the "type/file.json" tail, then the bare basename
        var slash = norm.LastIndexOf('/');
        if (slash >= 0)
        {
            var prevSlash = norm.LastIndexOf('/', slash - 1);
            var tail = prevSlash >= 0 ? norm[(prevSlash + 1)..] : norm;
            if (byPath.TryGetValue(tail, out var p)) return p;
        }
        var baseName = slash >= 0 ? norm[(slash + 1)..] : norm;
        return byBase.GetValueOrDefault(baseName);
    }

    // ------------------------------------------------------------- helpers ---

    private static bool LooksLikeIssue(string line) =>
        line.Contains("[Error", StringComparison.Ordinal) ||
        line.Contains("[Warning", StringComparison.Ordinal) ||
        line.Contains("[Fatal", StringComparison.Ordinal) ||
        line.Contains("#Error#", StringComparison.Ordinal) ||
        line.Contains("#Warning#", StringComparison.Ordinal) ||
        line.Contains("Exception", StringComparison.Ordinal);

    private static string Norm(string s) => s.Trim().ToLowerInvariant();
    private static string Name(ModEntry m) => m.DisplayName ?? m.Name;

    private static string Count(int errs, int warns) =>
        string.Join(" and ", new[]
        {
            errs > 0 ? $"{errs} error{(errs == 1 ? "" : "s")}" : null,
            warns > 0 ? $"{warns} warning{(warns == 1 ? "" : "s")}" : null,
        }.Where(x => x is not null));

    private static string Truncate(string s) => s.Length > 120 ? s[..117] + "…" : s;
}
