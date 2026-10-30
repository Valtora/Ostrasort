using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ostrasort;

/// <summary>A Thunderstore mod manager profile that ran the game: its own BepInEx folder holds the code mods.</summary>
public sealed record ManagerProfile(string Manager, string Name, string Root)
{
    public string BepInExDir => Path.Combine(Root, "BepInEx");
    public string Label => $"{Manager} profile '{Name}'";
}

/// <summary>
/// Finds the r2modman or Gale profile that ran the game. Both managers start
/// Ostranauts with BepInEx loaded from the profile folder instead of the game
/// folder, so the plugins, patchers and LogOutput.log of that session live in
/// the profile. Ostrasort reads them from there and never changes a profile:
/// installing and removing those mods stays the manager's job.
///
/// The profile counts as active only when it provably drove the game's last
/// session on this install: Player.log was written by this game folder, the
/// profile's LogOutput.log was written in the same session (within ten minutes
/// of Player.log), and it is newer than the game folder's own LogOutput.log.
/// Anything less certain leaves the game folder's BepInEx in charge.
///
/// Profile roots: r2modman keeps them in %APPDATA%\r2modmanPlus-local\Ostranauts\profiles
/// (its Thunderstore internal folder name). Gale keeps them in
/// %APPDATA%\com.kesomannen.gale\ostranauts\profiles, or under the data folder
/// set in its prefs.json. A custom r2modman data folder is not followed.
/// </summary>
public static class ManagerProfiles
{
    private static readonly TimeSpan SameSession = TimeSpan.FromMinutes(10);

    private static string AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    /// <summary>Every profile folder of both managers, by manager name.</summary>
    public static IEnumerable<(string Manager, string Dir)> AllProfileDirs(string? appData = null)
    {
        appData ??= AppData;
        var roots = new List<(string, string)>
        {
            ("r2modman", Path.Combine(appData, "r2modmanPlus-local", "Ostranauts", "profiles")),
            ("Gale", Path.Combine(GaleDataDir(appData), "ostranauts", "profiles")),
        };
        foreach (var (manager, root) in roots)
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> dirs;
            try { dirs = Directory.EnumerateDirectories(root).ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var d in dirs) yield return (manager, d);
        }
    }

    /// <summary>Gale's data folder: its prefs.json "dataDir" when set (a string or {"value": ...}), else the default.</summary>
    private static string GaleDataDir(string appData)
    {
        var app = Path.Combine(appData, "com.kesomannen.gale");
        try
        {
            var prefs = Path.Combine(app, "prefs.json");
            if (File.Exists(prefs))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(prefs));
                if (doc.RootElement.TryGetProperty("dataDir", out var d))
                {
                    var value = d.ValueKind == JsonValueKind.String ? d.GetString()
                        : d.ValueKind == JsonValueKind.Object && d.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()
                        : null;
                    if (value is { Length: > 0 } && Directory.Exists(value)) return value;
                }
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return app;
    }

    private static readonly Regex MonoPath = new(@"^Mono path\[0\] = '(.+?)[/\\]Ostranauts_Data[/\\]Managed'", RegexOptions.Compiled);

    /// <summary>The profile that ran this install's last session, or null.</summary>
    public static ManagerProfile? Active(string gameRoot, string playerLogPath, string? appData = null)
    {
        try
        {
            if (!File.Exists(playerLogPath)) return null;
            var writer = File.ReadLines(playerLogPath).Take(20).Select(l => MonoPath.Match(l)).FirstOrDefault(m => m.Success)?.Groups[1].Value;
            if (writer is null || !SamePath(writer, gameRoot)) return null;
            var session = File.GetLastWriteTimeUtc(playerLogPath);
            var ownLog = Path.Combine(gameRoot, "BepInEx", "LogOutput.log");
            var ownWrite = File.Exists(ownLog) ? File.GetLastWriteTimeUtc(ownLog) : DateTime.MinValue;

            ManagerProfile? best = null;
            var bestWrite = DateTime.MinValue;
            foreach (var (manager, dir) in AllProfileDirs(appData))
            {
                var log = Path.Combine(dir, "BepInEx", "LogOutput.log");
                if (!File.Exists(log)) continue;
                var w = File.GetLastWriteTimeUtc(log);
                if (w <= ownWrite || (w - session).Duration() > SameSession || w <= bestWrite) continue;
                best = new ManagerProfile(manager, Path.GetFileName(dir), dir);
                bestWrite = w;
            }
            return best;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                      Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);
}
