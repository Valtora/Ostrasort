using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Ostrasort;

/// <summary>Locates the game install and the folders Ostrasort reads.</summary>
public sealed class GameEnv
{
    public const string DefaultGameRoot = @"C:\Program Files (x86)\Steam\steamapps\common\Ostranauts";

    public required string GameRoot { get; init; }
    public required string CoreDataDir { get; init; }   // StreamingAssets\data
    public required string ModsDir { get; init; }       // holds loading_order.json + local mods
    public string? WorkshopContentDir { get; init; }    // steamapps\workshop\content\1022980
    public string? InstalledVersion { get; init; }      // e.g. "0.15.1.6"

    public string LoadingOrderPath => Path.Combine(ModsDir, "loading_order.json");

    public static GameEnv Locate(string? gameRootOverride)
    {
        var root = gameRootOverride ?? DefaultGameRoot;
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException(
                $"Game install not found at '{root}'. Pass --game <path-to-Ostranauts-folder>.");

        var dataDir = Path.Combine(root, "Ostranauts_Data");
        var modsDir = Path.Combine(dataDir, "Mods");

        // settings.json can relocate the Mods folder via strPathMods
        var settings = Path.Combine(
            Environment.GetEnvironmentVariable("USERPROFILE") ?? "",
            @"AppData\LocalLow\Blue Bottle Games\Ostranauts\settings.json");
        if (File.Exists(settings))
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
            CoreDataDir = Path.Combine(dataDir, "StreamingAssets", "data"),
            ModsDir = modsDir,
            WorkshopContentDir = workshop,
            InstalledVersion = ReadInstalledVersion(dataDir),
        };
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
