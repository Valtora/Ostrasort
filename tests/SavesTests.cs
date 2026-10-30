using System.IO;
using Ostrasort;
using Xunit;

namespace Ostrasort.Tests;

public class SavesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "OstraSaves_" + Guid.NewGuid().ToString("N"));
    private string Saves => Path.Combine(_root, "Saves");
    private string Backups => Path.Combine(_root, "backups");

    public SavesTests()
    {
        Directory.CreateDirectory(Saves);
        WriteSave("alice_1", "v1", DateTime.UtcNow.AddHours(-2));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void WriteSave(string name, string content, DateTime writtenUtc)
    {
        var dir = Path.Combine(Saves, name);
        Directory.CreateDirectory(dir);
        var f = Path.Combine(dir, name + ".zip");
        File.WriteAllText(f, content);
        File.SetLastWriteTimeUtc(f, writtenUtc);
    }

    [Fact]
    public void Create_SkipsWhenNothingChanged_AndKeepsTheNewestThree()
    {
        var first = SaveBackups.Create("manual", Saves, Backups, nowUtc: DateTime.UtcNow.AddHours(-1));
        Assert.NotNull(first);
        Assert.Null(SaveBackups.Create("manual", Saves, Backups));   // no save changed since

        for (var i = 0; i < 4; i++)
        {
            WriteSave("alice_1", "v" + (i + 2), DateTime.UtcNow.AddMinutes(-50 + i * 10));
            Assert.NotNull(SaveBackups.Create("patch change", Saves, Backups, nowUtc: DateTime.UtcNow.AddMinutes(-45 + i * 10)));
        }

        var list = SaveBackups.List(Backups);
        Assert.Equal(SaveBackups.Keep, list.Count);
        Assert.All(list, b => Assert.Equal("patch change", b.Reason));
        Assert.True(list[0].TakenUtc > list[1].TakenUtc);
    }

    [Fact]
    public void Restore_PutsTheBackupBack_AndKeepsTheReplacedSaves()
    {
        var backup = SaveBackups.Create("manual", Saves, Backups, nowUtc: DateTime.UtcNow.AddHours(-1))!;
        WriteSave("alice_1", "ruined", DateTime.UtcNow);
        WriteSave("bob_2", "new", DateTime.UtcNow);

        var before = SaveBackups.Restore(backup, Saves, Backups);

        Assert.Equal("v1", File.ReadAllText(Path.Combine(Saves, "alice_1", "alice_1.zip")));
        Assert.False(Directory.Exists(Path.Combine(Saves, "bob_2")));
        Assert.NotNull(before);
        Assert.True(File.Exists(backup.Path));   // the restored-from backup survives the pre-restore backup's pruning
        Assert.False(Directory.Exists(Path.Combine(_root, "Saves.ostrasort-old")));
    }

    private static Analysis With(params string[] raws) => new()
    {
        Registered = raws.Select(r => new ModEntry
        {
            Raw = r, Kind = r == "core" ? EntryKind.Core : EntryKind.Local, Name = r.Split('|')[0],
            Dir = r == "core" ? null : r.Split('|')[0], DisplayName = r.Split('|')[0],
            Disabled = r.EndsWith("|disabled"),
        }).ToList(),
    };

    private static readonly GameEnv Env = new()
    {
        GameRoot = @"C:\g", DiscoveredVia = "test", CoreDataDir = @"C:\g\core", ModsDir = @"C:\g\Mods",
    };

    [Fact]
    public void SavesWrittenDuringALaunch_LinkToItsModSet_AndWarnWhenThoseModsAreGone()
    {
        var store = new SaveModSets.Store();
        var start = DateTime.UtcNow.AddMinutes(-30);
        SaveModSets.RecordLaunch(store, With("core", "Ships|edit", "Lamps|edit", "Old|disabled"), Env, start);
        WriteSave("alice_1", "v2", start.AddMinutes(10));
        SaveModSets.CloseFinished(store, gameRunning: false, playerLogWriteUtc: start.AddMinutes(20), DateTime.UtcNow);

        Assert.True(SaveModSets.Link(store, Saves, DateTime.UtcNow));
        Assert.Equal(start, store.Saves["alice_1"].LaunchedUtc);

        var now = With("core", "Ships|edit", "Lamps|disabled");
        Assert.Equal(new[] { "Lamps" }, SaveModSets.MissingNow(store.Saves["alice_1"], now).Select(e => e.DisplayName));
        SaveModSets.Annotate(now, Env, store, Saves);
        Assert.Contains(now.Warnings, w => w.Contains("'alice_1'") && w.Contains("Lamps"));
    }

    [Fact]
    public void SaveWrittenOutsideAnyLaunch_StaysUnlinked()
    {
        var store = new SaveModSets.Store();
        var start = DateTime.UtcNow.AddMinutes(-30);
        SaveModSets.RecordLaunch(store, With("core"), Env, start);
        SaveModSets.CloseFinished(store, gameRunning: false, playerLogWriteUtc: start.AddMinutes(5), DateTime.UtcNow);
        WriteSave("alice_1", "v2", start.AddMinutes(20));   // a later session started from Steam

        Assert.False(SaveModSets.Link(store, Saves, DateTime.UtcNow));
        Assert.Empty(store.Saves);
    }
}
