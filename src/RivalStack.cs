using System.IO;

namespace Ostrasort;

/// <summary>
/// Evidence that a rival, non-Workshop mod stack is installed: Robyn's
/// <b>OstraAutoloader</b> and/or the <b>FFU</b> (Fight for Universe: Beyond
/// Reach) MonoMod framework distributed on Thunderstore. Both ship their own
/// load-order management - the autoloader regenerates <c>loading_order.json</c>
/// itself at every launch - so they and Ostrasort contend for the same file.
///
/// Ostrasort is Steam-Workshop-first and deliberately does not support the
/// Thunderstore/FFU world. When this is detected the GUI blocks with a modal
/// and the console refuses to write (override: <c>--allow-rival-stack</c>).
/// </summary>
public sealed record RivalStack(bool Autoloader, bool Ffu, bool MonoMod, IReadOnlyList<string> Evidence)
{
    /// <summary>Short phrase naming the strongest signal, for one-line messages.</summary>
    public string Summary =>
        Ffu ? "FFU MonoMod framework"
        : Autoloader && MonoMod ? "OstraAutoloader + MonoMod patches"
        : Autoloader ? "Robyn's OstraAutoloader"
        : MonoMod ? "MonoMod patches"
        : "Thunderstore mod stack";

    /// <summary>
    /// Scans the BepInEx install for the autoloader plugin, MonoMod patches, and
    /// Autoload.Meta.toml mods (which live under BepInEx\plugins or the Mods
    /// folder, both outside where the game's own load order reaches). Returns
    /// null for a clean Steam-Workshop-only install.
    /// </summary>
    public static RivalStack? Detect(GameEnv env)
    {
        var bep = env.BepInExDir;
        if (!Directory.Exists(bep)) return null;

        var evidence = new List<string>();
        bool autoloader = false, ffu = false, monoMod = false;

        // 1) OstraAutoloader - the actual load-order rival: its plugin DLL, and/or
        //    the Autoload.Meta.toml files that mark autoloader-managed mods.
        var plugins = Path.Combine(bep, "plugins");
        if (Directory.Exists(plugins))
        {
            var autoDll = SafeFiles(plugins, "*.dll")
                .FirstOrDefault(f => Path.GetFileName(f).Contains("Autoloader", StringComparison.OrdinalIgnoreCase));
            if (autoDll is not null)
            {
                autoloader = true;
                evidence.Add($"OstraAutoloader plugin: {Rel(env.GameRoot, autoDll)}");
            }

            var metas = SafeFiles(plugins, "Autoload.Meta.toml").ToList();
            if (metas.Count > 0)
            {
                autoloader = true;
                evidence.Add($"{metas.Count} autoloader mod(s) under BepInEx\\plugins (Autoload.Meta.toml)");
            }
        }

        // 2) MonoMod patches - FFU ships Assembly-CSharp.*.mm.dll in BepInEx\monomod.
        var monomod = Path.Combine(bep, "monomod");
        if (Directory.Exists(monomod))
        {
            var mm = SafeFiles(monomod, "*.mm.dll").Select(f => Path.GetFileName(f)).OfType<string>().ToList();
            if (mm.Count > 0)
            {
                monoMod = true;
                evidence.Add($"{mm.Count} MonoMod patch(es) in BepInEx\\monomod: {string.Join(", ", mm.Take(4))}");
                if (mm.Any(n => n.Contains("FFU", StringComparison.OrdinalIgnoreCase)))
                {
                    ffu = true;
                    evidence.Add("FFU (Fight for Universe: Beyond Reach) MonoMod core present");
                }
            }
        }

        // 3) Autoload.Meta.toml can also sit under the game's own Mods folder.
        if (Directory.Exists(env.ModsDir) &&
            SafeFiles(env.ModsDir, "Autoload.Meta.toml").FirstOrDefault() is { } modsMeta)
        {
            autoloader = true;
            evidence.Add($"autoloader mod in the Mods folder: {Rel(env.ModsDir, modsMeta)}");
        }

        return evidence.Count > 0 ? new RivalStack(autoloader, ffu, monoMod, evidence) : null;
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
