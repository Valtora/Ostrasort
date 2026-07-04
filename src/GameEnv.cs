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

    public static GameEnv Locate(string? gameRootOverride)
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

        var dataDir = Path.Combine(root, "Ostranauts_Data");
        var modsDir = Path.Combine(dataDir, "Mods");

        // settings.json can relocate the Mods folder via strPathMods - but only
        // honor that for the auto-detected install: with an explicit --game the
        // caller means THAT install's own Mods folder (e.g. a test fixture)
        var settings = Path.Combine(
            Environment.GetEnvironmentVariable("USERPROFILE") ?? "",
            @"AppData\LocalLow\Blue Bottle Games\Ostranauts\settings.json");
        if (gameRootOverride is null && File.Exists(settings))
        {
            try
            {
                var custom = JsonNode.Parse(File.ReadAllText(settings))?["strPathMods"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(custom) && Directory.Exists(custom))
                    modsDir = custom;
            }
            catch { /* unreadable settings.json is not Ostrasort's problem */ }
        }

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

    public static bool IsGameRunning() =>
        System.Diagnostics.Process.GetProcessesByName("Ostranauts").Length > 0;
}
