using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Ostrasort;

/// <summary>
/// Where <see cref="GameEnv.Locate(string?, string?, LocateSources)"/> looks on a
/// machine: the Steam install folder, the default install path, and the game's own
/// settings.json and Player.log. <see cref="Host"/> is the real machine; tests pass
/// their own so discovery runs without the registry or the player's files.
/// </summary>
public sealed record LocateSources(string? SteamPath, string? DefaultRoot, string SettingsPath, string PlayerLogPath)
{
    public static LocateSources Host() =>
        new(GameEnv.SteamPathFromRegistry(), GameEnv.DefaultGameRoot, GameEnv.GameSettingsPath, GameEnv.PlayerLogPath);
}

/// <summary>Locates the game install and the folders Ostrasort reads.</summary>
public sealed class GameEnv
{
    public const string DefaultGameRoot = @"C:\Program Files (x86)\Steam\steamapps\common\Ostranauts";

    /// <summary>Ostranauts' Steam app id: its workshop folder and appmanifest_1022980.acf.</summary>
    public const string SteamAppId = "1022980";

    public required string GameRoot { get; init; }
    public required string DiscoveredVia { get; init; }
    public required string CoreDataDir { get; init; }   // StreamingAssets\data
    public required string ModsDir { get; init; }       // holds loading_order.json + local mods
    public string? WorkshopContentDir { get; init; }    // steamapps\workshop\content\1022980
    public string? InstalledVersion { get; init; }      // e.g. "0.15.1.6"

    /// <summary>
    /// The parts of a real install that <see cref="GameRoot"/> lacks (relative
    /// paths, see <see cref="MissingInstallPartsOf"/>), filled by <see cref="Locate(string?, string?, LocateSources)"/>.
    /// Empty for a complete install, and for a hand-built env (tests).
    /// </summary>
    public IReadOnlyList<string> MissingInstallParts { get; init; } = [];

    public bool IsCompleteInstall => MissingInstallParts.Count == 0;

    /// <summary>The game's strPathMods as settings.json holds it, when it was read (auto-detect only).</summary>
    public string? StrPathMods { get; init; }

    /// <summary>
    /// The other Ostranauts folder whose own Mods folder strPathMods names, when that
    /// is not <see cref="GameRoot"/> (see <see cref="OtherInstallRoot"/>). Null otherwise.
    /// </summary>
    public string? StrPathModsOtherRoot { get; init; }

    /// <summary>True when <see cref="ModsDir"/> came from strPathMods rather than the default under the game.</summary>
    public bool ModsDirFromStrPathMods { get; init; }

    public string LoadingOrderPath => Path.Combine(ModsDir, "loading_order.json");

    private static string LocalLow => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        @"AppData\LocalLow\Blue Bottle Games\Ostranauts");

    /// <summary>The game's own log (Unity), in the per-user LocalLow folder.</summary>
    public static string PlayerLogPath => Path.Combine(LocalLow, "Player.log");

    /// <summary>The game's user settings (strPathMods among them), next to Player.log.</summary>
    public static string GameSettingsPath => Path.Combine(LocalLow, "settings.json");

    /// <summary>The BepInEx folder in the game install (mod loader, plugins, patchers, monomod). Every write goes here.</summary>
    public string BepInExDir => Path.Combine(GameRoot, "BepInEx");

    /// <summary>The r2modman or Gale profile that ran the game's last session, when there is one (see <see cref="ManagerProfiles"/>).</summary>
    public ManagerProfile? Manager { get; init; }

    /// <summary>
    /// Where the code mods that actually load are read from: the manager
    /// profile's BepInEx when one ran the game, else the game folder's.
    /// Read-only: nothing is ever written into a profile.
    /// </summary>
    public string CodeBepInExDir => Manager?.BepInExDir ?? BepInExDir;

    /// <summary>BepInEx's log (code-mod loading), wherever BepInEx last ran from.</summary>
    public string BepInExLogPath => Path.Combine(CodeBepInExDir, "LogOutput.log");

    /// <summary>True for a path inside the game's BepInEx folder or the active manager profile's.</summary>
    public bool IsUnderBepInEx(string path) =>
        IsUnder(path, BepInExDir) || (Manager is { } p && IsUnder(path, p.BepInExDir));

    /// <summary>True for a path inside the active manager profile, which Ostrasort must not change.</summary>
    public bool IsInManagerProfile(string path) => Manager is { } p && IsUnder(path, p.Root);

    private static bool IsUnder(string path, string dir) =>
        path.Replace('/', '\\').StartsWith(dir.Replace('/', '\\').TrimEnd('\\') + '\\', StringComparison.OrdinalIgnoreCase);

    public static GameEnv Locate(string? gameRootOverride, string? modsDirOverride = null) =>
        Locate(gameRootOverride, modsDirOverride, LocateSources.Host());

    /// <summary>
    /// Locate against explicit <paramref name="sources"/>. An explicit game root
    /// (--game or a saved install) stays lenient and only needs an Ostranauts_Data
    /// folder. Auto-detect accepts only a real install (see <see cref="IsRealInstall"/>),
    /// so a leftover folder holding nothing but Mods can never be picked over the
    /// install the game actually runs from.
    /// </summary>
    public static GameEnv Locate(string? gameRootOverride, string? modsDirOverride, LocateSources sources)
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
        else if (LocateViaSteam(sources.SteamPath, sources.PlayerLogPath) is { } steamHit)
        {
            (root, via) = steamHit;
        }
        else if (sources.DefaultRoot is { } defaultRoot && IsRealInstall(defaultRoot))
        {
            root = defaultRoot;
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
        // settings.json sits beside Player.log (SpecialFolder, not the USERPROFILE
        // env var, which can be unset and would silently no-op)
        string? strPathMods = null, otherRoot = null;
        var fromStrPathMods = false;
        if (gameRootOverride is null && modsDirOverride is null && File.Exists(sources.SettingsPath))
        {
            try
            {
                strPathMods = ReadStrPathMods(sources.SettingsPath);
                if (ResolveModsDir(strPathMods) is { } relocated)
                {
                    modsDir = relocated;
                    fromStrPathMods = true;
                }
                // the game keeps reading there even when it is another install's
                // folder, so ModsDir follows it; this only records the fact
                otherRoot = OtherInstallRoot(strPathMods, root);
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
            Manager = ManagerProfiles.Active(root, sources.PlayerLogPath),
            MissingInstallParts = MissingInstallPartsOf(root),
            StrPathMods = strPathMods,
            StrPathModsOtherRoot = otherRoot,
            ModsDirFromStrPathMods = fromStrPathMods,
        };
    }

    /// <summary>
    /// What a game folder needs before Ostrasort can analyse against it: the base
    /// game's data (every comparison with vanilla) and globalgamemanagers (the
    /// installed version). A folder the game recreated only to hold its mod list
    /// has neither. Returns the missing parts as paths relative to the folder.
    /// </summary>
    public static IReadOnlyList<string> MissingInstallPartsOf(string gameRoot)
    {
        var missing = new List<string>();
        if (!Directory.Exists(Path.Combine(gameRoot, "Ostranauts_Data", "StreamingAssets", "data")))
            missing.Add(@"Ostranauts_Data\StreamingAssets\data");
        if (!File.Exists(Path.Combine(gameRoot, "Ostranauts_Data", "globalgamemanagers")))
            missing.Add(@"Ostranauts_Data\globalgamemanagers");
        return missing;
    }

    /// <summary>
    /// A folder that holds the game itself (its globalgamemanagers or Ostranauts.exe),
    /// as opposed to one left behind with only Ostranauts_Data\Mods in it. Steam's
    /// uninstall leaves files it did not install, and the game recreates the folder
    /// of a stale strPathMods whenever it saves its mod list.
    /// </summary>
    public static bool IsRealInstall(string gameRoot) =>
        File.Exists(Path.Combine(gameRoot, "Ostranauts_Data", "globalgamemanagers"))
        || File.Exists(Path.Combine(gameRoot, "Ostranauts.exe"));

    /// <summary>
    /// The Ostranauts folder whose own Mods folder <paramref name="strPathMods"/>
    /// names, when that is not <paramref name="gameRoot"/>, else null. The game sets
    /// strPathMods once, on its first run, to its own Ostranauts_Data folder +
    /// "/Mods/loading_order.json", and keeps it in settings.json through every
    /// reinstall and file check. So a value naming another folder's
    /// Ostranauts_Data\Mods is almost always left over from an old install. A mods
    /// folder moved anywhere else is a deliberate setup and returns null.
    /// </summary>
    public static string? OtherInstallRoot(string? strPathMods, string gameRoot)
    {
        if (string.IsNullOrWhiteSpace(strPathMods)) return null;
        var path = strPathMods.Trim().Replace('/', '\\');
        if (!Path.IsPathFullyQualified(path)) return null;
        try { path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }

        // the game stores the loading_order.json file; a folder is accepted too (ResolveModsDir)
        var mods = Path.GetFileName(path).Equals("Mods", StringComparison.OrdinalIgnoreCase) ? path : Path.GetDirectoryName(path);
        var data = mods is null ? null : Path.GetDirectoryName(mods);
        if (mods is null || data is null
            || !Path.GetFileName(mods).Equals("Mods", StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(data).Equals("Ostranauts_Data", StringComparison.OrdinalIgnoreCase))
            return null;
        var other = Path.GetDirectoryName(data);
        return other is null || SamePath(other, gameRoot) ? null : other;
    }

    /// <summary>Two paths name the same folder (full paths, trailing separator and case ignored).</summary>
    public static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                      Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// strPathMods from the game's settings.json, or null. The game writes that file
    /// as a JSON array of settings objects (the player's own is strName
    /// "UserSettings"), not as a bare object, so indexing the root by name finds
    /// nothing. A bare object is still accepted. Throws on an unreadable file.
    /// </summary>
    public static string? ReadStrPathMods(string settingsPath)
    {
        var root = GameJson.ParseNode(File.ReadAllText(settingsPath), null,
            new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var objects = root switch
        {
            JsonObject o => new List<JsonObject> { o },
            JsonArray a => a.OfType<JsonObject>()
                .OrderByDescending(o => o["strName"] is JsonValue n && n.TryGetValue<string>(out var name) && name == "UserSettings")
                .ToList(),
            _ => new List<JsonObject>(),
        };
        foreach (var o in objects)
            if (o["strPathMods"] is JsonValue v && v.TryGetValue<string>(out var value)) return value;
        return null;
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

    /// <summary>The Steam install folder from the registry, or null.</summary>
    internal static string? SteamPathFromRegistry()
    {
        try
        {
            return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string
                ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
        }
        catch { return null; /* no registry access -> fall through to the default path */ }
    }

    private static readonly Regex VdfPath = new("\"path\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);
    private static readonly Regex VdfApps = new("\"apps\"\\s*\\{([^}]*)\\}", RegexOptions.Compiled);
    private static readonly Regex VdfGameApp = new($"^\\s*\"{SteamAppId}\"\\s+\"[^\"]*\"\\s*$", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// Standard Steam locator: the Steam folder, then every library listed in its
    /// libraryfolders.vdf, probed for steamapps\common\Ostranauts. Handles installs
    /// on any drive without the user telling us anything.
    ///
    /// Only a real install counts (<see cref="IsRealInstall"/>). When several
    /// libraries hold one, the library Steam records the game in wins (its
    /// steamapps\appmanifest_1022980.acf, or the game in that library's "apps"
    /// block), then the one that wrote Player.log last, then probe order.
    /// </summary>
    public static (string Root, string Via)? LocateViaSteam(string? steamPath, string? playerLogPath = null)
    {
        if (string.IsNullOrWhiteSpace(steamPath)) return null;
        var steam = Path.TrimEndingDirectorySeparator(Path.GetFullPath(steamPath.Replace('/', '\\')));

        var libraries = new List<string> { steam };
        var recorded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // libraries whose "apps" block lists the game
        foreach (var vdf in new[]
                 {
                     Path.Combine(steam, "steamapps", "libraryfolders.vdf"),
                     Path.Combine(steam, "config", "libraryfolders.vdf"),
                 })
        {
            if (!File.Exists(vdf)) continue;
            var text = File.ReadAllText(vdf);
            var paths = VdfPath.Matches(text);
            for (var i = 0; i < paths.Count; i++)
            {
                string lib;
                try { lib = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Regex.Unescape(paths[i].Groups[1].Value))); }
                catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
                { continue; /* malformed escape or path in vdf - skip that entry */ }
                libraries.Add(lib);
                // a library's block runs on to the next "path" key, and its "apps"
                // block lists every game Steam installed in that library
                var end = i + 1 < paths.Count ? paths[i + 1].Index : text.Length;
                if (VdfApps.Match(text, paths[i].Index, end - paths[i].Index) is { Success: true } apps
                    && VdfGameApp.IsMatch(apps.Groups[1].Value))
                    recorded.Add(lib);
            }
            break;   // the steamapps copy is authoritative on modern clients
        }

        var installs = libraries.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(lib => (Lib: lib, Root: Path.Combine(lib, "steamapps", "common", "Ostranauts")))
            .Where(c => IsRealInstall(c.Root))
            .ToList();
        if (installs.Count == 0) return null;

        var steamRecords = installs
            .Where(c => recorded.Contains(c.Lib)
                        || File.Exists(Path.Combine(c.Lib, "steamapps", $"appmanifest_{SteamAppId}.acf")))
            .ToList();
        var pool = steamRecords.Count > 0 ? steamRecords : installs;
        var lastRun = playerLogPath is null ? null : LastRunRoot(playerLogPath);
        var pick = lastRun is null ? pool[0] : pool.FirstOrDefault(c => SamePath(c.Root, lastRun), pool[0]);
        return (pick.Root, $"Steam library at {pick.Lib}");
    }

    private static readonly Regex MonoPath = new(@"^Mono path\[0\] = '(.+?)[/\\]Ostranauts_Data[/\\]Managed'", RegexOptions.Compiled);

    /// <summary>
    /// The game folder that wrote Player.log, from its "Mono path[0]" line (Unity
    /// prints it in the first few lines of every launch), or null.
    /// </summary>
    public static string? LastRunRoot(string playerLogPath)
    {
        try
        {
            if (!File.Exists(playerLogPath)) return null;
            return File.ReadLines(playerLogPath).Take(20).Select(l => MonoPath.Match(l)).FirstOrDefault(m => m.Success)?.Groups[1].Value;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
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
