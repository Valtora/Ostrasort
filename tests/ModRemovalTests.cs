using System.IO;
using Ostrasort;
using Xunit;

namespace Ostrasort.Tests;

public class ModRemovalTests : IDisposable
{
    private readonly string _root;
    private readonly GameEnv _env;

    public ModRemovalTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "OstraRm_" + Guid.NewGuid().ToString("N"));
        var mods = Path.Combine(_root, "Mods");
        Directory.CreateDirectory(Path.Combine(mods, "ModA"));
        File.WriteAllText(Path.Combine(mods, "ModA", "mod_info.json"), """[{"strName":"Mod A"}]""");
        File.WriteAllText(Path.Combine(mods, "loading_order.json"),
            """[{"strName":"Mod Loading Order","aLoadOrder":["core","ModA|edit"]}]""");
        _env = new GameEnv
        {
            GameRoot = _root, DiscoveredVia = "test",
            CoreDataDir = Path.Combine(_root, "core"), ModsDir = mods,
        };
    }

    public void Dispose()
    {
        // clear ReadOnly first so a failing ReadOnly test doesn't make teardown hit
        // the very bug under test (which would mask the real assertion failure)
        foreach (var i in new DirectoryInfo(_root).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
            i.Attributes &= ~FileAttributes.ReadOnly;
        Directory.Delete(_root, recursive: true);
    }

    private ModEntry ModA() => ModEntry.Parse("ModA|edit", _env);

    [Fact]
    public void Park_RenamesTheFolderAndUnregisters()
    {
        var result = ModRemoval.RemoveLocal(_env, ModA(), delete: false);

        Assert.False(result.Deleted);
        Assert.False(Directory.Exists(Path.Combine(_env.ModsDir, "ModA")));
        Assert.True(Directory.Exists(Path.Combine(_env.ModsDir, "ModA.disabled")));
        Assert.Equal(new[] { "ModA|edit" }, result.Unregistered);
        Assert.Equal(new[] { "core" }, LoadOrderFile.Read(_env.LoadingOrderPath).Order);
    }

    [Fact]
    public void Delete_RemovesTheFolderAndUnregisters()
    {
        var result = ModRemoval.RemoveLocal(_env, ModA(), delete: true);

        Assert.True(result.Deleted);
        Assert.False(Directory.Exists(Path.Combine(_env.ModsDir, "ModA")));
        Assert.False(Directory.Exists(Path.Combine(_env.ModsDir, "ModA.disabled")));
        Assert.Equal(new[] { "core" }, LoadOrderFile.Read(_env.LoadingOrderPath).Order);
    }

    [Fact]
    public void Delete_SucceedsWhenFoldersAreReadOnly()
    {
        // mods unzipped from a download often carry the ReadOnly attribute on their
        // folders; plain Directory.Delete(recursive) can't remove those and would
        // leave a half-deleted mod (files gone, folders + "access denied" left).
        var data = Path.Combine(_env.ModsDir, "ModA", "data");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "items.json"), "[]");
        new DirectoryInfo(data).Attributes |= FileAttributes.ReadOnly;
        new DirectoryInfo(Path.Combine(_env.ModsDir, "ModA")).Attributes |= FileAttributes.ReadOnly;

        var result = ModRemoval.RemoveLocal(_env, ModA(), delete: true);

        Assert.True(result.Deleted);
        Assert.Empty(Directory.GetDirectories(_env.ModsDir));   // ModA gone, and no leftover husk
        Assert.Equal(new[] { "core" }, LoadOrderFile.Read(_env.LoadingOrderPath).Order);
    }

    [Fact]
    public void Unregister_DropsEveryMarkerVariant()
    {
        File.WriteAllText(Path.Combine(_env.ModsDir, "loading_order.json"),
            """[{"strName":"Mod Loading Order","aLoadOrder":["core","ModA","ModA|disabled","ModA|edit"]}]""");

        ModRemoval.RemoveLocal(_env, ModA(), delete: true);

        Assert.Equal(new[] { "core" }, LoadOrderFile.Read(_env.LoadingOrderPath).Order);
    }

    [Fact]
    public void RefusesWorkshopAndPatch()
    {
        var ws = new ModEntry { Raw = @"C:\ws\123", Kind = EntryKind.Workshop, Name = "123", Dir = null };
        Assert.Throws<InvalidOperationException>(() => ModRemoval.RemoveLocal(_env, ws, delete: true));

        var patchDir = Path.Combine(_env.ModsDir, Patcher.FolderName);
        Directory.CreateDirectory(patchDir);
        File.WriteAllText(Path.Combine(patchDir, Patcher.MarkerFile), "{}");
        var patch = new ModEntry { Raw = Patcher.FolderName, Kind = EntryKind.Local, Name = Patcher.FolderName, Dir = patchDir };
        Assert.Throws<InvalidOperationException>(() => ModRemoval.RemoveLocal(_env, patch, delete: true));
    }
}
