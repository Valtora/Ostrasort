using System.IO;
using Ostrasort;
using Xunit;

namespace Ostrasort.Tests;

/// <summary>
/// The install-level warnings Engine.Analyze puts ahead of every other: a game
/// folder that is not a complete install (no base game to compare against, and
/// no false "BepInEx is NOT installed"), and a strPathMods left pointing at
/// another Ostranauts folder's Mods. Built on the same fake Steam layout as
/// <see cref="SteamLocateTests"/>.
/// </summary>
public class InstallCheckTests : IDisposable
{
    private readonly FakeSteam _steam = new();

    public void Dispose() => _steam.Dispose();

    /// <summary>A Workshop-style code mod (a plugin DLL, no data) outside the game folder.</summary>
    private string CodeMod()
    {
        var dir = Path.Combine(_steam.Library("Steam Games"), "steamapps", "workshop", "content", GameEnv.SteamAppId, "111");
        Directory.CreateDirectory(Path.Combine(dir, "BepInEx", "plugins"));
        File.WriteAllText(Path.Combine(dir, "BepInEx", "plugins", "FakePlugin.dll"), "x");
        File.WriteAllText(Path.Combine(dir, "mod_info.json"), """[{"strName":"Fake Code Mod"}]""");
        return dir;
    }

    [Fact]
    public void LeftoverFolder_IsReportedIncomplete_WithTheEmptyCoreIndex_AndNoBepInExAlarm()
    {
        var leftover = _steam.LeftoverFolder(_steam.SteamPath, CodeMod());
        var env = GameEnv.Locate(leftover);   // a saved install or --game pointing at it stays allowed

        var a = Engine.Analyze(env).Analysis;

        Assert.NotNull(a.IncompleteInstallWarning);
        Assert.Equal(a.IncompleteInstallWarning, a.Warnings[0]);   // ahead of everything it undermines
        Assert.Equal(
            $"{env.GameRoot} is not a complete Ostranauts install (Ostranauts_Data\\StreamingAssets\\data and " +
            "Ostranauts_Data\\globalgamemanagers are missing). Ostrasort read 0 base game objects from it, so it has " +
            "nothing to compare your mods with, and the conflict, merge and game update checks are not reliable. " +
            "Use Manage next to Installation to add the folder the game runs from (in Steam, right click Ostranauts " +
            "and choose Manage, then Browse local files), or pass it with --game from a terminal.",
            a.IncompleteInstallWarning);
        Assert.DoesNotContain(a.Warnings, w => w.Contains("BepInEx is NOT installed"));
        Assert.DoesNotContain(a.Warnings, w => w.Contains("bridge patcher is missing"));
    }

    [Fact]
    public void CompleteInstall_WithoutBepInEx_StillRaisesTheBepInExWarning()
    {
        var root = _steam.RealInstall(_steam.SteamPath);
        FakeSteam.WriteLoadOrder(Path.Combine(root, "Ostranauts_Data", "Mods"), CodeMod());

        var a = Engine.Analyze(GameEnv.Locate(root)).Analysis;

        Assert.Null(a.IncompleteInstallWarning);
        Assert.Contains(a.Warnings, w => w.Contains("BepInEx is NOT installed"));
    }

    [Fact]
    public void ReportedLayout_WarnsAboutStrPathMods_AndFindsBepInExInTheRealInstall()
    {
        var games = _steam.Library("Steam Games");
        var leftover = _steam.LeftoverFolder(_steam.SteamPath, CodeMod());
        var real = _steam.RealInstall(games);
        Directory.CreateDirectory(Path.Combine(real, "BepInEx", "core"));
        _steam.WriteVdf((_steam.SteamPath, []), (games, [GameEnv.SteamAppId]));
        _steam.WriteSettings(FakeSteam.StrPathModsFor(leftover));
        var env = GameEnv.Locate(null, null, _steam.Sources());

        var a = Engine.Analyze(env).Analysis;

        Assert.Null(a.IncompleteInstallWarning);
        Assert.DoesNotContain(a.Warnings, w => w.Contains("BepInEx is NOT installed"));
        Assert.Equal(a.StrPathModsWarning, a.Warnings[0]);
        var ownMods = Path.Combine(env.GameRoot, "Ostranauts_Data", "Mods");
        Assert.Equal(
            $"The game reads its mod list from {FakeSteam.StrPathModsFor(leftover)}, which is the Mods folder of a " +
            $"different Ostranauts folder ({leftover}), not of the install Ostrasort opened ({env.GameRoot}). " +
            "This is usually left over from an old install, because the game keeps strPathMods in its settings.json " +
            "through every reinstall. Ostrasort reads and writes the same folder as the game, so your mods still load. " +
            "To go back to this install's own Mods folder, close the game, copy loading_order.json and any mod folders " +
            $"you added by hand from {Path.Combine(leftover, "Ostranauts_Data", "Mods")} to {ownMods}, set strPathMods " +
            $"in settings.json to {FakeSteam.StrPathModsFor(env.GameRoot)} (forward slashes, exactly as shown), then " +
            "start the game again and reopen Ostrasort. The Files tab of the game's Options shows the current path, " +
            "and its Settings button opens the folder that holds settings.json.",
            a.StrPathModsWarning);
        // the mod list it analysed is the one the game reads
        Assert.Contains(a.Registered, m => m.DisplayName == "Fake Code Mod");
    }

    [Fact]
    public void StrPathModsIntoAFolderThatIsGone_SaysTheGameLoadsNoMods()
    {
        var real = _steam.RealInstall(_steam.SteamPath, appManifest: true);
        _steam.WriteVdf((_steam.SteamPath, []));
        var gone = FakeSteam.GameIn(Path.Combine(_steam.Dir, "OldDrive"));
        _steam.WriteSettings(FakeSteam.StrPathModsFor(gone));
        var env = GameEnv.Locate(null, null, _steam.Sources());

        var warning = Engine.Analyze(env).Analysis.StrPathModsWarning;

        Assert.NotNull(warning);
        Assert.StartsWith($"The game reads its mod list from {FakeSteam.StrPathModsFor(gone)}, the Mods folder of a " +
                          $"different Ostranauts folder ({gone}), and that folder does not exist, so the game loads " +
                          "no mods at all. Ostrasort opens this install's own Mods folder " +
                          $"({Path.Combine(env.GameRoot, "Ostranauts_Data", "Mods")}) instead, which the game is not reading.",
                          warning);
        Assert.Contains($"set strPathMods in settings.json to {FakeSteam.StrPathModsFor(env.GameRoot)}", warning);
    }

    [Fact]
    public void StrPathModsIntoAFolderThatIsGone_AndNoListHere_ExplainsWhyInsteadOfLaunchTheGame()
    {
        var real = _steam.RealInstall(_steam.SteamPath, appManifest: true);
        File.Delete(Path.Combine(real, "Ostranauts_Data", "Mods", "loading_order.json"));   // the game never wrote here
        _steam.WriteVdf((_steam.SteamPath, []));
        _steam.WriteSettings(FakeSteam.StrPathModsFor(FakeSteam.GameIn(Path.Combine(_steam.Dir, "OldDrive"))));
        var env = GameEnv.Locate(null, null, _steam.Sources());

        var e = Assert.Throws<FileNotFoundException>(() => Engine.Analyze(env));

        Assert.Equal($"loading_order.json not found at '{env.LoadingOrderPath}'. {Engine.StrPathModsText(env)}", e.Message);
        Assert.DoesNotContain("Launch the game once", e.Message);
    }

    [Fact]
    public void StrPathModsOfTheInstallItself_RaisesNothing()
    {
        var real = _steam.RealInstall(_steam.SteamPath, appManifest: true);
        _steam.WriteVdf((_steam.SteamPath, []));
        _steam.WriteSettings(FakeSteam.StrPathModsFor(real));

        var a = Engine.Analyze(GameEnv.Locate(null, null, _steam.Sources())).Analysis;

        Assert.Null(a.StrPathModsWarning);
        Assert.Null(a.IncompleteInstallWarning);
        Assert.DoesNotContain(a.Warnings, w => w.Contains("strPathMods"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NewWarnings_UseOnlyFullStopsCommasAndBrackets(bool exists)
    {
        // user-facing text rule: no colons, semicolons, dashes or slashes in the prose
        // (paths and the strPathMods value are data, so they are cut out first)
        var real = _steam.RealInstall(_steam.SteamPath, appManifest: true);
        _steam.WriteVdf((_steam.SteamPath, []));
        var other = exists ? _steam.LeftoverFolder(_steam.Library("Old")) : FakeSteam.GameIn(Path.Combine(_steam.Dir, "Gone"));
        _steam.WriteSettings(FakeSteam.StrPathModsFor(other));
        var env = GameEnv.Locate(null, null, _steam.Sources());
        var incomplete = Engine.IncompleteInstallText(GameEnv.Locate(_steam.LeftoverFolder(_steam.Library("Husk"))), 0);

        foreach (var text in new[] { Engine.StrPathModsText(env)!, incomplete })
        {
            var prose = System.Text.RegularExpressions.Regex.Replace(text, @"\S*[\\/]\S*", "");
            Assert.DoesNotMatch("[:;!/]|–|—| - ", prose);
        }
    }
}

/// <summary>
/// On a game folder that is not a complete install Ostrasort only reports: the
/// GUI turns the fixes off (the reason text is what its buttons show) and the
/// CLI refuses before writing anything. Drives the CLI through Program.Run,
/// which skips Main's legacy data move, so no real user data is touched. Not
/// run in parallel, because it captures the process-wide console.
/// </summary>
[Collection("Console")]
public class IncompleteInstallGateTests : IDisposable
{
    private readonly FakeSteam _steam = new();
    private readonly string _leftover;
    private readonly string _order;

    public IncompleteInstallGateTests()
    {
        // a dead entry, so a suggested order exists and --apply has something to write
        _leftover = _steam.LeftoverFolder(_steam.SteamPath, "GoneMod");
        _order = Path.Combine(_leftover, "Ostranauts_Data", "Mods", "loading_order.json");
    }

    public void Dispose() => _steam.Dispose();

    private string Root => GameEnv.Locate(_leftover).GameRoot;

    private string Refusal(string action) =>
        $@"{Root} is not a complete Ostranauts install (Ostranauts_Data\StreamingAssets\data and " +
        $@"Ostranauts_Data\globalgamemanagers are missing), so Ostrasort will not {action} there. Without the base " +
        "game, and without the folder the game runs from, its merges and suggestions cannot be trusted. Pick that " +
        "folder with --game, or with Manage next to Installation in the window, then run the command again. " +
        "Reports still run, and nothing was written.";

    private static (int Code, string Out) RunCli(params string[] args)
    {
        var old = Console.Out;
        var sw = new StringWriter();
        Console.SetOut(sw);
        try { return (Program.Run(args), sw.ToString()); }
        finally { Console.SetOut(old); }
    }

    [Fact]
    public void FixesBlockedReason_IsWhatTheDisabledButtonsShow()
    {
        Assert.Equal(
            "Turned off because this game folder is not a complete Ostranauts install, so there is no base game to " +
            "merge mods against. The first warning on the Warnings tab says how to pick the folder the game runs from.",
            Engine.FixesBlockedReason(GameEnv.Locate(_leftover)));
        Assert.Null(Engine.FixesBlockedReason(GameEnv.Locate(_steam.RealInstall(_steam.Library("Real")))));
    }

    [Fact]
    public void Patch_IsRefusedAndNothingIsWritten()
    {
        var before = File.ReadAllText(_order);

        var e = Assert.Throws<InvalidOperationException>(() => RunCli("--headless", "--patch", "--game", _leftover));

        Assert.Equal(Refusal("build the compatibility patch"), e.Message);
        Assert.Equal(before, File.ReadAllText(_order));
        Assert.False(Directory.Exists(Path.Combine(_leftover, "Ostranauts_Data", "Mods", "OstrasortPatch")));
    }

    [Fact]
    public void ApplySuggestedOrder_IsRefusedAndNothingIsWritten()
    {
        Assert.True(Engine.Analyze(GameEnv.Locate(_leftover)).Analysis.OrderChanged);   // there is a suggestion to refuse
        var before = File.ReadAllText(_order);

        var e = Assert.Throws<InvalidOperationException>(() => RunCli("--headless", "--apply", "--game", _leftover));

        Assert.Equal(Refusal("apply the suggested load order"), e.Message);
        Assert.Equal(before, File.ReadAllText(_order));
        Assert.False(File.Exists(_order + ".bak"));
    }

    [Fact]
    public void Report_StillRuns_AndLeadsWithTheIncompleteInstallWarning()
    {
        var (code, output) = RunCli("--headless", "--game", _leftover);

        Assert.Equal(2, code);   // the suggested order is still reported as actionable
        Assert.Contains($"! {Root} is not a complete Ostranauts install", output);
    }

    [Fact]
    public void Unpatch_IsNotRefused()
    {
        var (_, output) = RunCli("--headless", "--unpatch", "--game", _leftover);
        Assert.Contains("--unpatch: no Ostrasort Patch is installed - nothing to remove.", output);
    }

    [Fact]
    public void Patch_OnACompleteInstall_IsNotRefused()
    {
        var real = _steam.RealInstall(_steam.Library("Real"));

        var (code, output) = RunCli("--headless", "--patch", "--game", real);

        Assert.Equal(0, code);
        Assert.Contains("--patch: no mergeable conflicts exist - nothing to patch.", output);
    }
}

[CollectionDefinition("Console", DisableParallelization = true)]
public class ConsoleCollection;
