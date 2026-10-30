using System.IO;
using Ostrasort;
using Xunit;

namespace Ostrasort.Tests;

public class ManagerProfilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "OstraMgr_" + Guid.NewGuid().ToString("N"));
    private string AppData => Path.Combine(_root, "appdata");
    private string Game => Path.Combine(_root, "Ostranauts");
    private string PlayerLog => Path.Combine(_root, "Player.log");
    private readonly DateTime _session = DateTime.UtcNow.AddHours(-1);

    public ManagerProfilesTests()
    {
        Directory.CreateDirectory(Path.Combine(Game, "Ostranauts_Data"));
        File.WriteAllText(PlayerLog, $"Mono path[0] = '{Game.Replace('\\', '/')}/Ostranauts_Data/Managed'\n");
        File.SetLastWriteTimeUtc(PlayerLog, _session);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Profile(string relRoot, string name, DateTime logWrite)
    {
        var dir = Path.Combine(AppData, relRoot, "profiles", name);
        Directory.CreateDirectory(Path.Combine(dir, "BepInEx", "plugins"));
        var log = Path.Combine(dir, "BepInEx", "LogOutput.log");
        File.WriteAllText(log, "log");
        File.SetLastWriteTimeUtc(log, logWrite);
        return dir;
    }

    [Fact]
    public void TheProfileThatRanTheLastSession_IsActive()
    {
        Profile(@"r2modmanPlus-local\Ostranauts", "Old", _session.AddDays(-3));
        var gale = Profile(@"com.kesomannen.gale\ostranauts", "Default", _session.AddMinutes(-1));

        var active = ManagerProfiles.Active(Game, PlayerLog, AppData);

        Assert.Equal(new ManagerProfile("Gale", "Default", gale), active);
    }

    [Fact]
    public void AGameRunWithItsOwnBepInEx_HasNoActiveProfile()
    {
        Profile(@"r2modmanPlus-local\Ostranauts", "Default", _session.AddMinutes(-2));
        var own = Path.Combine(Game, "BepInEx", "LogOutput.log");
        Directory.CreateDirectory(Path.GetDirectoryName(own)!);
        File.WriteAllText(own, "log");
        File.SetLastWriteTimeUtc(own, _session);

        Assert.Null(ManagerProfiles.Active(Game, PlayerLog, AppData));
    }

    [Fact]
    public void APlayerLogFromAnotherInstall_HasNoActiveProfile()
    {
        Profile(@"r2modmanPlus-local\Ostranauts", "Default", _session);
        File.WriteAllText(PlayerLog, "Mono path[0] = 'D:/Other/Ostranauts/Ostranauts_Data/Managed'\n");
        Assert.Null(ManagerProfiles.Active(Game, PlayerLog, AppData));
    }

    [Fact]
    public void GalePrefs_CanMoveItsDataFolder()
    {
        var custom = Path.Combine(_root, "GaleData");
        Directory.CreateDirectory(Path.Combine(AppData, "com.kesomannen.gale"));
        File.WriteAllText(Path.Combine(AppData, "com.kesomannen.gale", "prefs.json"),
            "{\"dataDir\":{\"value\":\"" + custom.Replace("\\", "\\\\") + "\"}}");
        var dir = Path.Combine(custom, "ostranauts", "profiles", "Moved");
        Directory.CreateDirectory(dir);

        Assert.Contains(ManagerProfiles.AllProfileDirs(AppData), p => p.Manager == "Gale" && p.Dir == dir);
    }

    [Fact]
    public void AProfileDataMod_IsAPluginDirEntry_AndCannotBeRemoved()
    {
        var gale = Profile(@"com.kesomannen.gale\ostranauts", "Default", _session);
        var env = new GameEnv
        {
            GameRoot = Game, DiscoveredVia = "test", CoreDataDir = Path.Combine(Game, "core"), ModsDir = Path.Combine(Game, "Mods"),
            Manager = new ManagerProfile("Gale", "Default", gale),
        };
        var modDir = Path.Combine(gale, "BepInEx", "plugins", "FFU_Group-Mod");
        Directory.CreateDirectory(modDir);

        var m = ModEntry.Parse(modDir, env);

        Assert.Equal(EntryKind.PluginDir, m.Kind);
        Assert.Equal(Path.Combine(gale, "BepInEx", "LogOutput.log"), env.BepInExLogPath);
        var ex = Assert.Throws<InvalidOperationException>(() => ModRemoval.RemoveLocal(env, m, delete: true));
        Assert.Contains("Gale profile 'Default'", ex.Message);
        Assert.True(Directory.Exists(modDir));
    }
}
