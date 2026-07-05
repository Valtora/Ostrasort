using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace Ostrasort;

/// <summary>
/// The FFU / OstraAutoloader load groups, as declared in Autoload.Meta.toml.
/// The game loads WithVanilla content first, then the FFU core tier (Minor
/// Fixes Plus), then mods that require FFU.
/// </summary>
public enum FfuLoadGroup { WithVanilla, FFUCore, AfterFFU }

/// <summary>
/// A mod's Autoload.Meta.toml, the declarative ordering contract used by the
/// FFU ecosystem (and by Robyn's OstraAutoloader): a LoadGroup plus hard
/// dependencies keyed by the depended-on mod's strName. Ostrasort consumes it
/// as data - the file by itself is inert and does NOT mean the autoloader is
/// installed.
/// </summary>
public sealed class AutoloadMeta
{
    public const string FileName = "Autoload.Meta.toml";

    public FfuLoadGroup? Group { get; init; }
    public string? RawGroup { get; init; }
    /// <summary>Dependency strName -> version constraint ("ANY" or a version string).</summary>
    public Dictionary<string, string> Dependencies { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Problems { get; } = new();

    // "Key" = "value"   /   Key = "value"   (keys with spaces are quoted)
    private static readonly Regex KeyValue = new(
        """^\s*(?:"(?<qk>[^"]*)"|(?<k>[A-Za-z0-9_.\-]+))\s*=\s*"(?<v>[^"]*)"\s*(?:#.*)?$""",
        RegexOptions.Compiled);
    private static readonly Regex Section = new(@"^\s*\[(?<s>[^\]]+)\]\s*(?:#.*)?$", RegexOptions.Compiled);

    /// <summary>Reads a mod folder's Autoload.Meta.toml; null when the mod has none.</summary>
    public static AutoloadMeta? Read(string modDir)
    {
        var path = Path.Combine(modDir, FileName);
        if (!File.Exists(path)) return null;

        string? rawGroup = null;
        var problems = new List<string>();
        var deps = new List<(string Name, string Version)>();
        var section = "";

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            if (Section.Match(line) is { Success: true } s)
            {
                section = s.Groups["s"].Value.Trim();
                continue;
            }
            if (KeyValue.Match(line) is { Success: true } kv)
            {
                var key = kv.Groups["qk"].Success ? kv.Groups["qk"].Value : kv.Groups["k"].Value;
                var val = kv.Groups["v"].Value;
                if (section.Equals("dependencies", StringComparison.OrdinalIgnoreCase))
                    deps.Add((key, val));
                else if (key.Equals("LoadGroup", StringComparison.OrdinalIgnoreCase))
                    rawGroup = val;
                // FileType and unknown root keys are ignored
                continue;
            }
            problems.Add($"{FileName}: unparseable line '{line}'");
        }

        FfuLoadGroup? group = rawGroup?.Trim() switch
        {
            null => null,
            var g when g.Equals("WithVanilla", StringComparison.OrdinalIgnoreCase) => FfuLoadGroup.WithVanilla,
            var g when g.Equals("FFUCore", StringComparison.OrdinalIgnoreCase) => FfuLoadGroup.FFUCore,
            var g when g.Equals("AfterFFU", StringComparison.OrdinalIgnoreCase) => FfuLoadGroup.AfterFFU,
            _ => null,
        };
        if (rawGroup is not null && group is null)
            problems.Add($"{FileName}: unknown LoadGroup '{rawGroup}' (expected WithVanilla / FFUCore / AfterFFU)");

        var meta = new AutoloadMeta { Group = group, RawGroup = rawGroup };
        foreach (var (name, version) in deps)
            meta.Dependencies.TryAdd(name, version);
        meta.Problems.AddRange(problems);
        return meta;
    }
}

/// <summary>
/// Install-level FFU state. FFU itself (MonoMod patches in BepInEx\monomod +
/// ordinary data mods) is a SUPPORTED framework: Ostrasort applies its
/// ordering rules and merge semantics. The one true rival is Robyn's
/// <b>OstraAutoloader</b> plugin: it rewrites loading_order.json from scratch
/// at every game launch (keeping only Autoload.Meta.toml-tagged mods), so any
/// order Ostrasort writes would be undone and unmanaged local mods silently
/// dropped - writes are refused while it is active.
/// </summary>
public sealed class FfuContext
{
    /// <summary>The OstraAutoloader plugin DLL is installed - the actual rival. Writes are gated.</summary>
    public bool AutoloaderActive { get; init; }
    /// <summary>FFU MonoMod patches (Assembly-CSharp.FFU_*.mm.dll) are installed.</summary>
    public bool FrameworkPresent { get; init; }
    /// <summary>Any MonoMod patch DLLs exist in BepInEx\monomod (FFU or otherwise).</summary>
    public bool MonoModPatchesPresent { get; init; }
    /// <summary>The MonoMod patch loader is installed (BepInEx\core\MonoMod.dll / a MonoMod patcher).</summary>
    public bool MonoModLoaderPresent { get; init; }
    public List<string> FrameworkDlls { get; } = new();      // FFU *.mm.dll basenames
    public List<string> FrameworkVersions { get; } = new();  // distinct file versions among them
    public List<string> AutoloaderDlls { get; } = new();     // full paths - the disable action renames these
    public List<string> Evidence { get; } = new();
    /// <summary>Set by <see cref="FfuAnalysis.Classify"/>: at least one FFU-classified mod is present.</summary>
    public bool AnyFfuMods { get; set; }

    public string Summary =>
        AutoloaderActive ? "Robyn's OstraAutoloader"
        : FrameworkPresent ? "FFU (Fight for Universe: Beyond Reach)"
        : MonoModPatchesPresent ? "MonoMod patches"
        : "FFU-style mods";

    /// <summary>
    /// Scans the BepInEx install. Returns null on a clean, non-FFU install.
    /// Meta files alone do not create a context - they are handled per-mod by
    /// the scanner; <see cref="FfuAnalysis.Classify"/> creates a context later
    /// if FFU-classified mods exist without any framework files.
    /// </summary>
    public static FfuContext? Detect(GameEnv env)
    {
        var bep = env.BepInExDir;
        if (!Directory.Exists(bep)) return null;

        var evidence = new List<string>();
        var ffuDlls = new List<string>();
        var ffuVersions = new List<string>();
        bool autoloader = false, monoModPatches = false, framework = false;

        var autoDlls = new List<string>();
        var plugins = Path.Combine(bep, "plugins");
        if (Directory.Exists(plugins))
        {
            autoDlls = SafeFiles(plugins, "*.dll")
                .Where(f => Path.GetFileName(f).Contains("Autoloader", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (autoDlls.Count > 0)
            {
                autoloader = true;
                foreach (var f in autoDlls)
                    evidence.Add($"OstraAutoloader plugin: {Rel(env.GameRoot, f)}");
            }
        }

        var monomod = Path.Combine(bep, "monomod");
        if (Directory.Exists(monomod))
        {
            var mm = SafeFiles(monomod, "*.mm.dll").ToList();
            if (mm.Count > 0)
            {
                monoModPatches = true;
                var names = mm.Select(Path.GetFileName).OfType<string>().ToList();
                evidence.Add($"{mm.Count} MonoMod patch(es) in BepInEx\\monomod: " +
                             string.Join(", ", names.Take(4)) + (names.Count > 4 ? ", ..." : ""));
                foreach (var f in mm.Where(f => Path.GetFileName(f).Contains("FFU", StringComparison.OrdinalIgnoreCase)))
                {
                    framework = true;
                    ffuDlls.Add(Path.GetFileName(f));
                    var v = FileVersionOf(f);
                    if (v is not null && !ffuVersions.Contains(v)) ffuVersions.Add(v);
                }
                if (framework)
                    evidence.Add($"FFU framework core present ({ffuDlls.Count} FFU DLL(s)" +
                                 (ffuVersions.Count > 0 ? $", version {string.Join(" / ", ffuVersions)}" : "") + ")");
            }
        }

        if (!autoloader && !monoModPatches) return null;

        var loaderPresent =
            File.Exists(Path.Combine(bep, "core", "MonoMod.dll")) ||
            (Directory.Exists(Path.Combine(bep, "patchers")) &&
             SafeFiles(Path.Combine(bep, "patchers"), "*.dll")
                 .Any(f => Path.GetFileName(f).Contains("MonoMod", StringComparison.OrdinalIgnoreCase)));

        var ctx = new FfuContext
        {
            AutoloaderActive = autoloader,
            FrameworkPresent = framework,
            MonoModPatchesPresent = monoModPatches,
            MonoModLoaderPresent = loaderPresent,
        };
        ctx.FrameworkDlls.AddRange(ffuDlls);
        ctx.FrameworkVersions.AddRange(ffuVersions);
        ctx.AutoloaderDlls.AddRange(autoDlls);
        ctx.Evidence.AddRange(evidence);
        return ctx;
    }

    private static string? FileVersionOf(string path)
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(path).FileVersion;
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }
        catch { return null; }
    }

    private static IEnumerable<string> SafeFiles(string dir, string pattern)
    {
        try { return Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories); }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    private static string Rel(string root, string path)
    {
        try { return Path.GetRelativePath(root, path); }
        catch { return path; }
    }
}

/// <summary>
/// Post-scan FFU classification and hygiene. Decides which mods belong to the
/// FFU block (they must load after every non-FFU mod), resolves FFU "Patch"
/// mods to their targets, and emits the FFU-specific warnings and notices.
/// </summary>
public static class FfuAnalysis
{
    /// <summary>The mandatory first FFU mod ("Using FFU:BR DLL without using this mod breaks some part of the vanilla game").</summary>
    public const string MinorFixesPlus = "Minor Fixes Plus";

    private static readonly Regex PatchWord = new(@"\bpatch\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static void Classify(GameEnv env, Analysis a)
    {
        var mods = a.AllMods.Where(m => m.Kind != EntryKind.Core && m.Dir is not null).ToList();

        // ---- seed FFU-ness from each mod's own signals ----
        foreach (var m in mods)
        {
            if (m.Meta?.Group is FfuLoadGroup.FFUCore or FfuLoadGroup.AfterFFU)
            {
                m.IsFfu = true;
                m.FfuGroup = m.Meta.Group.Value;
                m.FfuSignals.Add($"Autoload.Meta.toml LoadGroup={m.Meta.RawGroup}");
            }
            if (string.Equals(m.DisplayName, MinorFixesPlus, StringComparison.OrdinalIgnoreCase))
            {
                m.IsFfu = true;
                m.FfuGroup = FfuLoadGroup.FFUCore;
                if (m.FfuSignals.Count == 0) m.FfuSignals.Add("it is Minor Fixes Plus (the FFU core-tier mod)");
            }
            if (m.UsesElasticApi && !m.IsFfu)
            {
                if (m.Meta?.Group is FfuLoadGroup.WithVanilla)
                    a.Warnings.Add($"'{m.DisplayName ?? m.Name}' declares LoadGroup=WithVanilla but uses the FFU " +
                                   $"modding API ({string.Join(", ", m.FfuSignals.Take(2))}) - treating it as FFU-dependent");
                m.IsFfu = true;
                m.FfuGroup = FfuLoadGroup.AfterFFU;
            }
        }

        // ---- propagate through declared dependencies (dep on an FFU mod = FFU mod) ----
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var m in mods.Where(m => !m.IsFfu && m.Meta is { Dependencies.Count: > 0 }))
            {
                if (m.Meta!.Group is FfuLoadGroup.WithVanilla) continue;   // author explicitly opted out
                foreach (var dep in m.Meta.Dependencies.Keys)
                {
                    var target = FindByStrName(mods, dep);
                    if (target?.IsFfu != true &&
                        !dep.Equals(MinorFixesPlus, StringComparison.OrdinalIgnoreCase)) continue;
                    m.IsFfu = true;
                    m.FfuGroup = FfuLoadGroup.AfterFFU;
                    m.FfuSignals.Add($"depends on FFU mod '{dep}' (Autoload.Meta.toml)");
                    changed = true;
                    break;
                }
            }
        }

        // ---- FFU "Patch" mods: load right after their target, remove after one use ----
        foreach (var m in mods.Where(m => m.IsFfu &&
                     (PatchWord.IsMatch(m.DisplayName ?? "") || PatchWord.IsMatch(m.Name))))
        {
            m.IsFfuPatch = true;
            // the target is the (unique) declared dependency that is not the FFU core tier
            var candidates = (m.Meta?.Dependencies.Keys ?? Enumerable.Empty<string>())
                .Select(d => FindByStrName(mods, d))
                .Where(t => t is not null && t != m && t.FfuGroup != FfuLoadGroup.FFUCore)
                .ToList();
            m.FfuPatchTarget = candidates.Count == 1 ? candidates[0] : NameHeuristicTarget(mods, m);
        }

        // ---- context reconciliation ----
        var anyFfu = mods.Any(m => m.IsFfu);
        if (anyFfu && a.Ffu is null)
            a.Ffu = new FfuContext();   // FFU-style mods without any framework files on disk
        if (a.Ffu is not null) a.Ffu.AnyFfuMods = anyFfu;

        Hygiene(env, a, mods);
    }

    private static ModEntry? FindByStrName(List<ModEntry> mods, string strName) =>
        mods.FirstOrDefault(m => string.Equals(m.DisplayName, strName, StringComparison.OrdinalIgnoreCase))
        ?? mods.FirstOrDefault(m => string.Equals(m.Name, strName, StringComparison.OrdinalIgnoreCase));

    /// <summary>"Some Mod Patch" / "Some Mod Compat Patch" -> the installed mod whose name is the longest leading match.</summary>
    private static ModEntry? NameHeuristicTarget(List<ModEntry> mods, ModEntry patch)
    {
        var pname = (patch.DisplayName ?? patch.Name).Trim();
        return mods
            .Where(m => m != patch)
            .Where(m =>
            {
                var n = (m.DisplayName ?? m.Name).Trim();
                return n.Length >= 4 && pname.StartsWith(n, StringComparison.OrdinalIgnoreCase);
            })
            .OrderByDescending(m => (m.DisplayName ?? m.Name).Length)
            .FirstOrDefault();
    }

    // ------------------------------------------------------------- hygiene ---

    private static void Hygiene(GameEnv env, Analysis a, List<ModEntry> mods)
    {
        var ctx = a.Ffu;
        if (ctx is null) return;

        // FFU updates require removing ALL old FFU files first - mixed versions = leftovers
        if (ctx.FrameworkVersions.Count > 1)
            a.Warnings.Add($"FFU DLLs in BepInEx\\monomod have mixed versions ({string.Join(" / ", ctx.FrameworkVersions)}) - " +
                           "an FFU update requires deleting ALL old FFU files first; remove the leftovers and reinstall one version");

        if (ctx.MonoModPatchesPresent && !ctx.MonoModLoaderPresent)
            a.Warnings.Add("MonoMod patches sit in BepInEx\\monomod but the MonoMod patch loader is not installed " +
                           "(BepInEx\\core\\MonoMod.dll missing) - FFU/MonoMod code will not load at all");

        if (ctx.FrameworkPresent)
        {
            var mfp = mods.FirstOrDefault(m =>
                string.Equals(m.DisplayName, MinorFixesPlus, StringComparison.OrdinalIgnoreCase));
            if (mfp is null)
                a.Warnings.Add($"the FFU framework is installed but '{MinorFixesPlus}' is missing - it is mandatory: " +
                               "using the FFU DLLs without it breaks parts of the vanilla game");
            else if (!mfp.Registered)
                a.Warnings.Add($"'{MinorFixesPlus}' exists on disk but is not registered in aLoadOrder - " +
                               "it is mandatory for FFU and must load as the first FFU mod");

            // FFU's MonoMod DLLs are compiled against ONE specific game build; a
            // mismatch merges stale copies of DataHandler + every Json* data class
            // into the newer assembly and typically breaks the game outright
            // (broken main menu, NullReferenceException loops). The FFU core-tier
            // mod's strGameVersion is the best available statement of the target.
            if (mfp?.GameVersion is { Length: > 0 } gv && env.InstalledVersion is { Length: > 0 } iv && gv != iv)
                a.Warnings.Add($"FFU VERSION MISMATCH: the installed FFU build targets game {gv} but this game is {iv}. " +
                               "FFU patches the game's data layer at IL level, so a mismatch usually breaks the game " +
                               "(broken main menu / NullReferenceException spam). Install the FFU build matching your " +
                               "game version, or remove the FFU files (BepInEx\\monomod) until one exists");
        }

        if (ctx.AnyFfuMods && !ctx.FrameworkPresent)
            a.Warnings.Add("FFU-dependent mod(s) are installed but the FFU framework (Assembly-CSharp.FFU_*.mm.dll " +
                           "in BepInEx\\monomod) is missing - their FFU-style data will not load correctly and " +
                           "partial-object entries would corrupt the objects they touch");

        foreach (var p in mods.Where(m => m.IsFfuPatch && m.Registered))
            a.Warnings.Add($"FFU patch mod '{p.DisplayName ?? p.Name}' applies once - " +
                           "remove it from the load order after the next game launch" +
                           (p.FfuPatchTarget is null ? " (Ostrasort could not identify which mod it patches)" : ""));

        // declared dependencies that are not installed at all
        foreach (var m in mods.Where(m => m.Meta is { Dependencies.Count: > 0 }))
            foreach (var dep in m.Meta!.Dependencies.Keys.Where(d => FindByStrName(mods, d) is null))
                a.Warnings.Add($"'{m.DisplayName ?? m.Name}' requires '{dep}' (Autoload.Meta.toml) but no such mod is installed");

        foreach (var m in mods.Where(m => m.Meta is { Problems.Count: > 0 }))
            foreach (var p in m.Meta!.Problems)
                a.Warnings.Add($"'{m.DisplayName ?? m.Name}': {p}");

        // removeIds vs another mod's claim on the same object: order-dependent outcome
        foreach (var remover in mods.Where(m => m.RemoveIds.Count > 0))
            foreach (var (type, id) in remover.RemoveIds)
                foreach (var other in mods.Where(o => o != remover && o.Claims.ContainsKey((type, id))))
                    a.Warnings.Add($"'{remover.DisplayName ?? remover.Name}' removes {type}/{id} (FFU removeIds) " +
                                   $"which '{other.DisplayName ?? other.Name}' also modifies - the outcome depends on " +
                                   $"load order; load '{other.DisplayName ?? other.Name}' after the remover to keep its version");
    }

    // ------------------------------------------------------------- disable ---

    /// <summary>
    /// The "hand the load order to Ostrasort" call to action: renames every
    /// detected autoloader DLL to <c>*.dll.disabled</c> so BepInEx no longer
    /// loads it. Fully reversible (rename it back). Returns the new paths.
    /// Callers gate on the game not running; a locked file throws IOException.
    /// </summary>
    public static List<string> DisableAutoloader(FfuContext ctx)
    {
        var renamed = new List<string>();
        foreach (var dll in ctx.AutoloaderDlls.Where(File.Exists))
        {
            var target = dll + ".disabled";
            if (File.Exists(target)) File.Delete(target);   // leftover from an earlier disable
            File.Move(dll, target);
            renamed.Add(target);
        }
        return renamed;
    }

    // ------------------------------------------------------------- notices ---

    /// <summary>The non-blocking informational lines for the GUI banner / console FFU section.</summary>
    public static List<string> Notices(Analysis a)
    {
        var lines = new List<string>();
        if (a.Ffu is not { } ctx) return lines;

        if (ctx.AutoloaderActive)
        {
            lines.Add("OstraAutoloader is installed - it rewrites loading_order.json from scratch at EVERY game " +
                      "launch, keeping only Autoload.Meta.toml-tagged mods: anything Ostrasort writes would be " +
                      "undone, and local mods (plus |edit / |disabled markers) get silently dropped. The game then " +
                      "re-appends subscribed Workshop mods at the END - after the FFU block, violating FFU's own " +
                      "ordering rule.");
            lines.Add("Ostrasort is read-only while the autoloader is active. Running both is unsupported - " +
                      "and the autoloader has not been meaningfully updated in about a year, while Ostrasort is " +
                      "actively maintained and covers everything it does (FFU load groups, dependencies, " +
                      "plugins-dir mods) plus Workshop and local mods.");
            lines.Add("Recommended: disable the autoloader and let Ostrasort manage the order - use the " +
                      "\"Disable OstraAutoloader\" button (or --disable-autoloader from a terminal; r2modman users " +
                      "should disable it in their profile instead). Disabling renames its DLL to .disabled, so it " +
                      "is fully reversible.");
            return lines;
        }

        lines.Add($"{ctx.Summary} detected - Ostrasort applies FFU's ordering rules: all non-FFU mods load first " +
                  "(the Ostrasort Patch closes that block), then Minor Fixes Plus, then FFU mods, dependency-sorted " +
                  "per their Autoload.Meta.toml.");
        if (ctx.FrameworkPresent)
            lines.Add("With FFU installed the game merges same-name objects field-by-field at load (instead of " +
                      "whole-object replacement) - the collision analysis below accounts for that. Plain loot/shop " +
                      "pools still replace wholesale unless a mod uses FFU's --ADD--/--DEL-- array commands.");
        if (a.AllMods.Any(m => m.IsFfuPatch))
            lines.Add("FFU 'Patch' mods are placed immediately after the mod they patch and should be removed " +
                      "from the load order after one game launch.");
        return lines;
    }
}
