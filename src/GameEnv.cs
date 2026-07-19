using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Ostrasort;

/// <summary>Locates the game install and the folders Ostrasort reads.</summary>
public sealed class GameEnv
{
    public const string DefaultGameRoot = @"C:\Program Files (x86)\Steam\steamapps\common\Ostranauts";

    public required string GameRoot { get; init; }
    public required string DiscoveredVia { get; init; }
    public required string CoreDataDir { get; init; }   // StreamingAssets\data
    public required string ModsDir { get; init; }       // holds loading_order.json + local mods
    public string? WorkshopContentDir { get; init; }    // steamapps\workshop\content\1022980
    public string? InstalledVersion { get; init; }      // e.g. "0.15.1.6"

    public string LoadingOrderPath => Path.Combine(ModsDir, "loading_order.json");

    /// <summary>The game's own log (Unity), in the per-user LocalLow folder.</summary>
    public static string PlayerLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        @"AppData\LocalLow\Blue Bottle Games\Ostranauts\Player.log");

    /// <summary>The BepInEx folder in the game install (mod loader, plugins, patchers, monomod).</summary>
    public string BepInExDir => Path.Combine(GameRoot, "BepInEx");

    /// <summary>BepInEx's log (code-mod loading), in the game folder.</summary>
    public string BepInExLogPath => Path.Combine(BepInExDir, "LogOutput.log");

    public static GameEnv Locate(string? gameRootOverride, string? modsDirOverride = null)
    {
        string root, via;
        if (gameRootOverride is not null)
        {
            root = Path.GetFullPath(gameRootOverride);
            via = "--game";
            if (!Directory.Exists(Path.Combine(root, "Ostranauts_Data")))
                throw new DirectoryNotFoundException(
                    $"'{root}' does not look like an Ostranauts install (no Ostranauts_Data folder inside it).");
        }
        else if (LocateViaSteam() is { } steamHit)
        {
            (root, via) = steamHit;
        }
        else if (Directory.Exists(Path.Combine(DefaultGameRoot, "Ostranauts_Data")))
        {
            root = DefaultGameRoot;
            via = "default install path";
        }
        else
        {
            throw new DirectoryNotFoundException(
                "Could not find the Ostranauts install (checked the Steam registry, every Steam " +
                "library, and the default path). Run with:  ostrasort --game \"<path to the " +
                "Ostranauts folder inside steamapps\\common>\"");
        }

        // Use the true on-disk case for the root (the Steam registry gives a
        // lowercase drive). Everything derived - workshop paths especially -
        // then matches what the game itself writes, so it won't re-add mods.
        root = PathCase.Canonical(root);

        var dataDir = Path.Combine(root, "Ostranauts_Data");
        var modsDir = Path.Combine(dataDir, "Mods");

        // settings.json can relocate the Mods folder via strPathMods - but only
        // honor that for a fully auto-detected install: an explicit --game / --mods
        // names a specific install's own folders (e.g. a test fixture). The game
        // stores strPathMods as the path to loading_order.json (a FILE), so accept
        // either a folder or a file whose parent folder is the real Mods dir.
        // same location scheme as PlayerLogPath (SpecialFolder, not the
        // USERPROFILE env var, which can be unset and would silently no-op)
        var settings = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            @"AppData\LocalLow\Blue Bottle Games\Ostranauts\settings.json");
        if (gameRootOverride is null && modsDirOverride is null && File.Exists(settings))
        {
            try
            {
                var custom = JsonNode.Parse(File.ReadAllText(settings))?["strPathMods"]?.GetValue<string>();
                if (ResolveModsDir(custom) is { } relocated) modsDir = relocated;
            }
            catch { /* unreadable settings.json is not Ostrasort's problem */ }
        }

        // An explicit mods-folder override wins over everything - this is the
        // two-install case (game on one disk, its mods folder on another). It
        // must exist; a missing override is surfaced, not silently ignored.
        if (modsDirOverride is not null)
        {
            var full = Path.GetFullPath(modsDirOverride);
            if (!Directory.Exists(full))
                throw new DirectoryNotFoundException(
                    $"'{full}' does not exist. Point Ostrasort at the mods folder that holds loading_order.json.");
            modsDir = full;
        }

        modsDir = PathCase.Canonical(modsDir);

        // steamapps\common\Ostranauts -> steamapps\workshop\content\1022980
        string? workshop = null;
        var steamapps = Path.GetDirectoryName(Path.GetDirectoryName(root));
        if (steamapps is not null)
        {
            var candidate = Path.Combine(steamapps, "workshop", "content", "1022980");
            if (Directory.Exists(candidate)) workshop = candidate;
        }

        return new GameEnv
        {
            GameRoot = root,
            DiscoveredVia = via,
            CoreDataDir = Path.Combine(dataDir, "StreamingAssets", "data"),
            ModsDir = modsDir,
            WorkshopContentDir = workshop,
            InstalledVersion = ReadInstalledVersion(dataDir),
        };
    }

    /// <summary>
    /// The game stores strPathMods as the path to loading_order.json (a FILE),
    /// not the folder. Accept either: a directory as-is, or a file whose parent
    /// directory exists. Returns null when neither resolves to a real folder.
    /// </summary>
    private static string? ResolveModsDir(string? strPathMods)
    {
        if (string.IsNullOrWhiteSpace(strPathMods)) return null;
        if (Directory.Exists(strPathMods)) return strPathMods;
        var dir = Path.GetDirectoryName(strPathMods);   // …/Mods/loading_order.json -> …/Mods
        return !string.IsNullOrEmpty(dir) && Directory.Exists(dir) ? dir : null;
    }

    /// <summary>
    /// Standard Steam locator: registry SteamPath -> parse libraryfolders.vdf ->
    /// probe every library for steamapps\common\Ostranauts. Handles installs on
    /// any drive without the user telling us anything.
    /// </summary>
    private static (string Root, string Via)? LocateViaSteam()
    {
        string? steam = null;
        try
        {
            steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string
                 ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
        }
        catch { /* no registry access -> fall through to the default path */ }
        if (string.IsNullOrWhiteSpace(steam)) return null;
        steam = Path.GetFullPath(steam.Replace('/', '\\'));

        var libraries = new List<string> { steam };
        foreach (var vdf in new[]
                 {
                     Path.Combine(steam, "steamapps", "libraryfolders.vdf"),
                     Path.Combine(steam, "config", "libraryfolders.vdf"),
                 })
        {
            if (!File.Exists(vdf)) continue;
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\""))
            {
                try { libraries.Add(Regex.Unescape(m.Groups[1].Value)); }
                catch (ArgumentException) { /* malformed escape in vdf - skip that entry */ }
            }
            break;   // the steamapps copy is authoritative on modern clients
        }

        foreach (var lib in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var candidate = Path.Combine(lib, "steamapps", "common", "Ostranauts");
            if (Directory.Exists(Path.Combine(candidate, "Ostranauts_Data")))
                return (candidate, $"Steam library at {lib}");
        }
        return null;
    }

    /// <summary>
    /// Application.version sits as a plain ASCII string inside globalgamemanagers
    /// (the same string the main menu and Player.log's "Early Access Build:" show).
    /// It tracks the install, not the last run.
    /// </summary>
    private static string? ReadInstalledVersion(string dataDir)
    {
        var ggm = Path.Combine(dataDir, "globalgamemanagers");
        if (!File.Exists(ggm)) return null;
        var text = Encoding.ASCII.GetString(File.ReadAllBytes(ggm));
        var m = Regex.Match(text, @"\d+\.\d+\.\d+\.\d+");
        return m.Success ? m.Value : null;
    }

    public static bool IsGameRunning()
    {
        // GetProcessesByName returns live Process objects - dispose them, or
        // every GUI poll while the game runs leaks a handle for the session
        var procs = System.Diagnostics.Process.GetProcessesByName("Ostranauts");
        foreach (var p in procs) p.Dispose();
        return procs.Length > 0;
    }
}
