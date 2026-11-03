using System.IO;
using Ostrasort;
using Xunit;

namespace Ostrasort.Tests;

/// <summary>
/// The mods-folder override (the two-install feature): an explicit --mods /
/// Installation mods folder wins and is validated. Uses a throwaway fixture so
/// it never touches the real install or the user's settings.json (an explicit
/// game root disables the strPathMods branch).
/// </summary>
public class GameEnvTests : IDisposable
{
    private readonly string _root;

    public GameEnvTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "OstraEnv_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "game", "Ostranauts_Data", "StreamingAssets", "data"));
        Directory.CreateDirectory(Path.Combine(_root, "game", "Ostranauts_Data", "Mods"));
        Directory.CreateDirectory(Path.Combine(_root, "othermods"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string GameRoot => Path.Combine(_root, "game");

    [Fact]
    public void Locate_ModsDirOverride_WinsAndDrivesLoadingOrderPath()
    {
        var otherMods = Path.Combine(_root, "othermods");
        var env = GameEnv.Locate(GameRoot, otherMods);

        Assert.Equal(PathCase.Canonical(otherMods), env.ModsDir);
        Assert.Equal(Path.Combine(env.ModsDir, "loading_order.json"), env.LoadingOrderPath);
        // the game root (hence core data) is unchanged - the two folders can live on different disks
        Assert.Equal(Path.Combine(GameRoot, "Ostranauts_Data", "StreamingAssets", "data"), env.CoreDataDir);
    }

    [Fact]
    public void Locate_NoModsOverride_UsesDefaultUnderTheGame()
    {
        var env = GameEnv.Locate(GameRoot);   // explicit game root => strPathMods ignored, default Mods
        Assert.Equal(PathCase.Canonical(Path.Combine(GameRoot, "Ostranauts_Data", "Mods")), env.ModsDir);
    }

    [Fact]
    public void Locate_MissingModsOverride_Throws()
    {
        Assert.Throws<DirectoryNotFoundException>(
            () => GameEnv.Locate(GameRoot, Path.Combine(_root, "does-not-exist")));
    }

    [Fact]
    public void Locate_ExplicitRoot_StaysLenientButReportsWhatIsMissing()
    {
        // no globalgamemanagers: --game and saved installs still open it, and the
        // gap is recorded for the incomplete-install warning
        var env = GameEnv.Locate(GameRoot);
        Assert.False(env.IsCompleteInstall);
        Assert.Equal(new[] { @"Ostranauts_Data\globalgamemanagers" }, env.MissingInstallParts);

        File.WriteAllText(Path.Combine(GameRoot, "Ostranauts_Data", "globalgamemanagers"), "\0\00.15.1.6\0");
        Assert.True(GameEnv.Locate(GameRoot).IsCompleteInstall);
    }
}

/// <summary>
/// Auto-detect through Steam, against a fake Steam layout so neither the
/// registry nor the player's own settings.json and Player.log are read. Models
/// the reported case: the Steam folder holds a leftover Ostranauts folder (only
/// Ostranauts_Data\Mods, kept alive by a stale strPathMods), and the game is
/// really installed in a second library.
/// </summary>
public class SteamLocateTests : IDisposable
{
    private readonly FakeSteam _steam = new();

    public void Dispose() => _steam.Dispose();

    [Fact]
    public void LocateViaSteam_SkipsALeftoverFolderInAnEarlierLibrary()
    {
        var games = _steam.Library("Steam Games");
        _steam.LeftoverFolder(_steam.SteamPath);
        var real = _steam.RealInstall(games);
        _steam.WriteVdf((_steam.SteamPath, []), (games, [GameEnv.SteamAppId]));

        var hit = GameEnv.LocateViaSteam(_steam.SteamPath);

        Assert.NotNull(hit);
        Assert.Equal(real, hit.Value.Root);
        Assert.Equal($"Steam library at {games}", hit.Value.Via);
    }

    [Fact]
    public void LocateViaSteam_OnlyALeftoverFolder_FindsNothingAndLocateFails()
    {
        _steam.LeftoverFolder(_steam.SteamPath);
        _steam.WriteVdf((_steam.SteamPath, [GameEnv.SteamAppId]));

        Assert.Null(GameEnv.LocateViaSteam(_steam.SteamPath));
        // the default-path fallback needs a real install too
        Assert.Throws<DirectoryNotFoundException>(() => GameEnv.Locate(null, null,
            _steam.Sources(defaultRoot: FakeSteam.GameIn(_steam.SteamPath))));
    }

    [Fact]
    public void LocateViaSteam_ExeAloneCountsAsARealInstall()
    {
        var root = FakeSteam.GameIn(_steam.SteamPath);
        Directory.CreateDirectory(Path.Combine(root, "Ostranauts_Data"));
        File.WriteAllText(Path.Combine(root, "Ostranauts.exe"), "");
        _steam.WriteVdf((_steam.SteamPath, []));

        Assert.Equal(root, GameEnv.LocateViaSteam(_steam.SteamPath)?.Root);
    }

    [Fact]
    public void LocateViaSteam_PrefersTheLibraryWithTheAppManifest()
    {
        var games = _steam.Library("Steam Games");
        _steam.RealInstall(_steam.SteamPath);   // a stale full copy, probed first
        var real = _steam.RealInstall(games, appManifest: true);
        _steam.WriteVdf((_steam.SteamPath, []), (games, []));

        Assert.Equal(real, GameEnv.LocateViaSteam(_steam.SteamPath)?.Root);
    }

    [Fact]
    public void LocateViaSteam_PrefersTheLibraryWhoseAppsBlockListsTheGame()
    {
        var games = _steam.Library("Steam Games");
        _steam.RealInstall(_steam.SteamPath);
        var real = _steam.RealInstall(games);
        // another game's size equal to the app id must not count as listing it
        _steam.WriteVdf((_steam.SteamPath, ["228980"]), (games, [GameEnv.SteamAppId]));

        Assert.Equal(real, GameEnv.LocateViaSteam(_steam.SteamPath)?.Root);
    }

    [Fact]
    public void LocateViaSteam_PlayerLogBreaksATieSteamCannot()
    {
        var games = _steam.Library("Steam Games");
        _steam.RealInstall(_steam.SteamPath);
        var real = _steam.RealInstall(games);
        _steam.WriteVdf((_steam.SteamPath, []), (games, []));
        var log = _steam.PlayerLog(real);

        Assert.Equal(FakeSteam.GameIn(_steam.SteamPath), GameEnv.LocateViaSteam(_steam.SteamPath)?.Root);   // probe order
        Assert.Equal(real, GameEnv.LocateViaSteam(_steam.SteamPath, log)?.Root);
    }

    [Fact]
    public void Locate_ReportedLayout_OpensTheRealInstallAndFollowsStrPathMods()
    {
        var games = _steam.Library("Steam Games");
        var leftover = _steam.LeftoverFolder(_steam.SteamPath);
        var real = _steam.RealInstall(games, version: "0.16.0.12");
        _steam.WriteVdf((_steam.SteamPath, []), (games, [GameEnv.SteamAppId]));
        _steam.WriteSettings(FakeSteam.StrPathModsFor(leftover));

        var env = GameEnv.Locate(null, null, _steam.Sources());

        Assert.Equal(PathCase.Canonical(real), env.GameRoot);
        Assert.True(env.IsCompleteInstall);
        Assert.Equal("0.16.0.12", env.InstalledVersion);
        // the game reads the leftover folder's list, so Ostrasort must read and write it too
        Assert.Equal(PathCase.Canonical(Path.Combine(leftover, "Ostranauts_Data", "Mods")), env.ModsDir);
        Assert.True(env.ModsDirFromStrPathMods);
        Assert.Equal(leftover, env.StrPathModsOtherRoot);
    }

    [Fact]
    public void Locate_StrPathModsIntoAFolderThatIsGone_KeepsTheOwnModsAndRecordsIt()
    {
        var games = _steam.Library("Steam Games");
        var real = _steam.RealInstall(games, appManifest: true);
        _steam.WriteVdf((_steam.SteamPath, []), (games, []));
        var gone = FakeSteam.GameIn(Path.Combine(_steam.Dir, "OldDrive"));
        _steam.WriteSettings(FakeSteam.StrPathModsFor(gone));

        var env = GameEnv.Locate(null, null, _steam.Sources());

        Assert.Equal(PathCase.Canonical(Path.Combine(real, "Ostranauts_Data", "Mods")), env.ModsDir);
        Assert.False(env.ModsDirFromStrPathMods);
        Assert.Equal(gone, env.StrPathModsOtherRoot);
    }

    [Fact]
    public void Locate_StrPathModsOfTheInstallItself_IsNotForeign()
    {
        var real = _steam.RealInstall(_steam.SteamPath);
        _steam.WriteVdf((_steam.SteamPath, [GameEnv.SteamAppId]));
        _steam.WriteSettings(FakeSteam.StrPathModsFor(real));

        var env = GameEnv.Locate(null, null, _steam.Sources());

        Assert.Equal(PathCase.Canonical(real), env.GameRoot);
        Assert.Null(env.StrPathModsOtherRoot);
    }

    [Fact]
    public void Locate_DeliberatelyMovedModsFolder_IsFollowedAndNotForeign()
    {
        var real = _steam.RealInstall(_steam.SteamPath);
        _steam.WriteVdf((_steam.SteamPath, [GameEnv.SteamAppId]));
        var moved = Path.Combine(_steam.Dir, "OstraMods");
        Directory.CreateDirectory(moved);
        _steam.WriteSettings(Path.Combine(moved, "loading_order.json").Replace('\\', '/'));

        var env = GameEnv.Locate(null, null, _steam.Sources());

        Assert.Equal(PathCase.Canonical(moved), env.ModsDir);
        Assert.True(env.ModsDirFromStrPathMods);
        Assert.Null(env.StrPathModsOtherRoot);
    }

    [Fact]
    public void ReadStrPathMods_ReadsTheArrayTheGameWrites_AndABareObject()
    {
        var path = Path.Combine(_steam.Dir, "s.json");
        // the shape DataHandler.SaveUserSettings writes (LitJson, pretty-printed)
        File.WriteAllText(path, """
            [
              {
                "strName" : "UserSettings",
                "strPathMods" : "C:/Steam Games/steamapps/common/Ostranauts/Ostranauts_Data/Mods/loading_order.json",
                "strDateFormat" : "DD_MM_YYYY"
              }
            ]
            """);
        Assert.Equal("C:/Steam Games/steamapps/common/Ostranauts/Ostranauts_Data/Mods/loading_order.json",
            GameEnv.ReadStrPathMods(path));

        File.WriteAllText(path, """{"strPathMods":"D:/Mods/loading_order.json"}""");
        Assert.Equal("D:/Mods/loading_order.json", GameEnv.ReadStrPathMods(path));

        File.WriteAllText(path, """[{"strName":"UserSettings"}]""");
        Assert.Null(GameEnv.ReadStrPathMods(path));
    }

    [Theory]
    [InlineData("H:/Program Files (x86)/Steam/steamapps/common/Ostranauts/Ostranauts_Data/Mods/loading_order.json",
                @"H:\Program Files (x86)\Steam\steamapps\common\Ostranauts")]
    [InlineData(@"H:\Old\Ostranauts\Ostranauts_Data\Mods", @"H:\Old\Ostranauts")]          // folder form, any install folder
    [InlineData(@"h:\old\ostranauts\ostranauts_data\mods\", @"h:\old\ostranauts")]
    [InlineData("C:/Steam Games/steamapps/common/Ostranauts/Ostranauts_Data/Mods/loading_order.json", null)]   // the install itself
    [InlineData(@"C:\STEAM GAMES\steamapps\common\Ostranauts\Ostranauts_Data\Mods\", null)]
    [InlineData("D:/OstraMods/loading_order.json", null)]                                   // moved on purpose
    [InlineData("D:/Ostranauts_Data/loading_order.json", null)]
    [InlineData("Mods/loading_order.json", null)]                                           // not a full path
    [InlineData("", null)]
    [InlineData(null, null)]
    public void OtherInstallRoot_NamesOnlyAnotherInstallsOwnModsFolder(string? strPathMods, string? expected)
    {
        Assert.Equal(expected, GameEnv.OtherInstallRoot(strPathMods, @"C:\Steam Games\steamapps\common\Ostranauts"));
    }
}

/// <summary>A fake Steam layout on disk: the Steam folder plus extra libraries, each of which can hold the game.</summary>
internal sealed class FakeSteam : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "OstraSteam_" + Guid.NewGuid().ToString("N"));
    public string SteamPath => Path.Combine(Dir, "Steam");
    public string SettingsPath => Path.Combine(Dir, "LocalLow", "settings.json");
    public string PlayerLogPath => Path.Combine(Dir, "LocalLow", "Player.log");

    public FakeSteam() => Directory.CreateDirectory(Path.Combine(SteamPath, "steamapps"));

    public void Dispose() => Directory.Delete(Dir, recursive: true);

    public string Library(string name)
    {
        var lib = Path.Combine(Dir, name);
        Directory.CreateDirectory(Path.Combine(lib, "steamapps"));
        return lib;
    }

    public static string GameIn(string library) => Path.Combine(library, "steamapps", "common", "Ostranauts");

    /// <summary>The game itself: base game data, globalgamemanagers carrying a version, a Mods folder listing only core.</summary>
    public string RealInstall(string library, string version = "0.15.1.6", bool appManifest = false)
    {
        var root = GameIn(library);
        Directory.CreateDirectory(Path.Combine(root, "Ostranauts_Data", "StreamingAssets", "data"));
        Directory.CreateDirectory(Path.Combine(root, "Ostranauts_Data", "Mods"));
        WriteLoadOrder(Path.Combine(root, "Ostranauts_Data", "Mods"));
        File.WriteAllText(Path.Combine(root, "Ostranauts_Data", "globalgamemanagers"), $"\0\0{version}\0\0");
        if (appManifest)
            File.WriteAllText(Path.Combine(library, "steamapps", $"appmanifest_{GameEnv.SteamAppId}.acf"),
                $"\"AppState\"\n{{\n\t\"appid\"\t\t\"{GameEnv.SteamAppId}\"\n}}\n");
        return root;
    }

    /// <summary>What a stale strPathMods leaves behind: Ostranauts_Data\Mods holding a mod list, and nothing else.</summary>
    public string LeftoverFolder(string library, params string[] loadOrder)
    {
        var root = GameIn(library);
        var mods = Path.Combine(root, "Ostranauts_Data", "Mods");
        Directory.CreateDirectory(mods);
        WriteLoadOrder(mods, loadOrder);
        return root;
    }

    public static void WriteLoadOrder(string modsDir, params string[] entries)
    {
        var list = string.Join(",", new[] { "core" }.Concat(entries).Select(e => System.Text.Json.JsonSerializer.Serialize(e)));
        File.WriteAllText(Path.Combine(modsDir, "loading_order.json"),
            $$"""[{"strName":"Mod Loading Order","aLoadOrder":[{{list}}]}]""");
    }

    /// <summary>steamapps\libraryfolders.vdf in the shape the Steam client writes, one block per library.</summary>
    public void WriteVdf(params (string Library, string[] Apps)[] libraries)
    {
        var sb = new System.Text.StringBuilder("\"libraryfolders\"\n{\n");
        for (var i = 0; i < libraries.Length; i++)
        {
            var (lib, apps) = libraries[i];
            sb.Append($"\t\"{i}\"\n\t{{\n\t\t\"path\"\t\t\"{lib.Replace(@"\", @"\\")}\"\n\t\t\"label\"\t\t\"\"\n");
            sb.Append("\t\t\"totalsize\"\t\t\"0\"\n\t\t\"apps\"\n\t\t{\n");
            // the value is the app's size on disk: give the other app the game's id as its
            // size, so a match on the value instead of the key would be caught
            foreach (var app in apps)
                sb.Append($"\t\t\t\"{app}\"\t\t\"{(app == GameEnv.SteamAppId ? "1539871081" : GameEnv.SteamAppId)}\"\n");
            sb.Append("\t\t}\n\t}\n");
        }
        sb.Append("}\n");
        File.WriteAllText(Path.Combine(SteamPath, "steamapps", "libraryfolders.vdf"), sb.ToString());
    }

    /// <summary>The game's settings.json (an array, as the game writes it) with this strPathMods.</summary>
    public void WriteSettings(string strPathMods)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath,
            $$"""[{"strName":"UserSettings","strPathMods":{{System.Text.Json.JsonSerializer.Serialize(strPathMods)}}}]""");
    }

    /// <summary>The value the game writes on first run from that folder.</summary>
    public static string StrPathModsFor(string gameRoot) =>
        gameRoot.Replace('\\', '/') + "/Ostranauts_Data/Mods/loading_order.json";

    /// <summary>A Player.log written by the game in <paramref name="gameRoot"/>.</summary>
    public string PlayerLog(string gameRoot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PlayerLogPath)!);
        File.WriteAllText(PlayerLogPath,
            $"Mono path[0] = '{gameRoot.Replace('\\', '/')}/Ostranauts_Data/Managed'\nInitialize engine version\n");
        return PlayerLogPath;
    }

    public LocateSources Sources(string? defaultRoot = null) => new(SteamPath, defaultRoot, SettingsPath, PlayerLogPath);
}
