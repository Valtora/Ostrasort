using System.IO;
using System.IO.Compression;
using Ostrasort;
using Xunit;

namespace Ostrasort.Tests;

public class ModInstallTests : IDisposable
{
    private readonly string _root;
    private readonly GameEnv _env;
    private int _zipSeq;

    public ModInstallTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "OstraInstall_" + Guid.NewGuid().ToString("N"));
        var mods = Path.Combine(_root, "Ostranauts_Data", "Mods");
        var core = Path.Combine(_root, "Ostranauts_Data", "StreamingAssets", "data");
        Directory.CreateDirectory(mods);
        Directory.CreateDirectory(core);
        File.WriteAllText(Path.Combine(mods, "loading_order.json"),
            """[{"strName":"Mod Loading Order","aLoadOrder":["core"]}]""");
        _env = new GameEnv
        {
            GameRoot = _root, DiscoveredVia = "test",
            CoreDataDir = core, ModsDir = mods,
        };
    }

    public void Dispose()
    {
        foreach (var i in new DirectoryInfo(_root).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
            i.Attributes &= ~FileAttributes.ReadOnly;
        Directory.Delete(_root, recursive: true);
    }

    /// <summary>Write a zip (entry path -> text content) to a temp file and return its path.</summary>
    private string Zip(params (string Path, string Content)[] entries)
    {
        var path = Path.Combine(_root, $"pkg{_zipSeq++}.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (p, content) in entries)
        {
            var e = zip.CreateEntry(p);
            using var w = new StreamWriter(e.Open());
            w.Write(content);
        }
        return path;
    }

    private string Mods(params string[] parts) => Path.Combine(new[] { _env.ModsDir }.Concat(parts).ToArray());
    private string Game(params string[] parts) => Path.Combine(new[] { _env.GameRoot }.Concat(parts).ToArray());

    // --------------------------------------------------------------- detection ---

    [Fact]
    public void ModInAFolder_InstallsUnderModsByStrName()
    {
        var zip = Zip(
            ("MyMod/mod_info.json", """[{"strName":"My Cool Mod"}]"""),
            ("MyMod/data/items/x.json", "[]"));

        var plan = ModInstall.Inspect(_env, zip);
        var c = Assert.Single(plan.Components);
        Assert.Equal("My Cool Mod", c.Name);
        Assert.Equal(ModInstall.ComponentKind.LocalMod, c.Kind);
        Assert.True(c.HasData);
        Assert.False(c.Exists);

        ModInstall.Execute(_env, plan);
        Assert.True(File.Exists(Mods("My Cool Mod", "mod_info.json")));
        Assert.True(File.Exists(Mods("My Cool Mod", "data", "items", "x.json")));
    }

    [Fact]
    public void GitHubWrapperFolder_IsStripped()
    {
        // GitHub "Download ZIP" wraps everything in <repo>-<branch>\
        var zip = Zip(
            ("Ostranauts-MyMod-main/mod_info.json", """[{"strName":"WrappedMod"}]"""),
            ("Ostranauts-MyMod-main/data/items/x.json", "[]"));

        var plan = ModInstall.Inspect(_env, zip);
        var c = Assert.Single(plan.Components);
        Assert.Equal("WrappedMod", c.Name);

        ModInstall.Execute(_env, plan);
        Assert.True(File.Exists(Mods("WrappedMod", "mod_info.json")));
        Assert.False(Directory.Exists(Mods("Ostranauts-MyMod-main")));
    }

    [Fact]
    public void ContentsAtArchiveRoot_UseStrName()
    {
        var zip = Zip(
            ("mod_info.json", """[{"strName":"RootMod"}]"""),
            ("data/items/x.json", "[]"));

        var plan = ModInstall.Inspect(_env, zip);
        var c = Assert.Single(plan.Components);
        Assert.Equal("RootMod", c.Name);

        ModInstall.Execute(_env, plan);
        Assert.True(File.Exists(Mods("RootMod", "data", "items", "x.json")));
    }

    [Fact]
    public void MultiModArchive_InstallsEveryMod()
    {
        var zip = Zip(
            ("ModA/mod_info.json", """[{"strName":"ModA"}]"""),
            ("ModA/data/a.json", "[]"),
            ("ModB/mod_info.json", """[{"strName":"ModB"}]"""),
            ("ModB/data/b.json", "[]"));

        var plan = ModInstall.Inspect(_env, zip);
        Assert.Equal(2, plan.Components.Count);

        ModInstall.Execute(_env, plan);
        Assert.True(File.Exists(Mods("ModA", "data", "a.json")));
        Assert.True(File.Exists(Mods("ModB", "data", "b.json")));
    }

    [Fact]
    public void NoModInfo_FallsBackToFolderNameWithWarning()
    {
        var zip = Zip(("LooseMod/data/items/x.json", "[]"));

        var plan = ModInstall.Inspect(_env, zip);
        var c = Assert.Single(plan.Components);
        Assert.Equal("LooseMod", c.Name);
        Assert.Contains(plan.Warnings, w => w.Contains("no mod_info.json"));

        ModInstall.Execute(_env, plan);
        Assert.True(File.Exists(Mods("LooseMod", "data", "items", "x.json")));
    }

    [Fact]
    public void BepInExBundle_InstallsIntoGameTreeAndIgnoresPackageMeta()
    {
        var zip = Zip(
            ("manifest.json", "{}"),
            ("README.md", "hi"),
            ("icon.png", "x"),
            ("BepInEx/plugins/Author-Mod/Author-Mod.dll", "dll"));

        var plan = ModInstall.Inspect(_env, zip);
        var c = Assert.Single(plan.Components);
        Assert.Equal(ModInstall.ComponentKind.BepInExBundle, c.Kind);
        Assert.Equal("Author-Mod", c.Name);
        Assert.False(c.HasData);

        ModInstall.Execute(_env, plan);
        Assert.True(File.Exists(Game("BepInEx", "plugins", "Author-Mod", "Author-Mod.dll")));
        Assert.False(File.Exists(Game("manifest.json")));        // package metadata is not part of BepInEx
        Assert.False(File.Exists(Game("README.md")));
    }

    [Fact]
    public void BepInExDataMod_IsMarkedRegistrable()
    {
        var zip = Zip(
            ("BepInEx/plugins/Foo/mod_info.json", """[{"strName":"Foo"}]"""),
            ("BepInEx/plugins/Foo/data/items/x.json", "[]"));

        var plan = ModInstall.Inspect(_env, zip);
        var c = Assert.Single(plan.Components);
        Assert.Equal(ModInstall.ComponentKind.BepInExBundle, c.Kind);
        Assert.True(c.HasData);
        Assert.Contains(c.RegisterDirs, d =>
            Path.GetFullPath(d).Equals(Path.GetFullPath(Game("BepInEx", "plugins", "Foo")), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CombinedDataAndCodeMod_SplitsBetweenModsAndBepInEx()
    {
        var zip = Zip(
            ("Combo/mod_info.json", """[{"strName":"Combo"}]"""),
            ("Combo/data/items/x.json", "[]"),
            ("Combo/BepInEx/plugins/P/P.dll", "dll"));

        var plan = ModInstall.Inspect(_env, zip);
        Assert.Equal(2, plan.Components.Count);
        Assert.Contains(plan.Components, c => c.Kind == ModInstall.ComponentKind.LocalMod && c.Name == "Combo");
        Assert.Contains(plan.Components, c => c.Kind == ModInstall.ComponentKind.BepInExBundle);

        ModInstall.Execute(_env, plan);
        Assert.True(File.Exists(Mods("Combo", "data", "items", "x.json")));
        Assert.False(Directory.Exists(Mods("Combo", "BepInEx")));           // not extracted into the Mods folder
        Assert.True(File.Exists(Game("BepInEx", "plugins", "P", "P.dll")));  // routed to the game tree
    }

    // ---------------------------------------------------------------- safety ---

    [Fact]
    public void PathTraversalEntry_IsRefused()
    {
        var zip = Zip(
            ("MyMod/mod_info.json", """[{"strName":"Evil"}]"""),
            ("MyMod/../../evil.txt", "pwned"));

        Assert.Throws<InvalidDataException>(() => ModInstall.Inspect(_env, zip));
    }

    [Fact]
    public void NotAZip_IsRefused()
    {
        var bogus = Path.Combine(_root, "not-a-zip.zip");
        File.WriteAllText(bogus, "this is not a zip");
        Assert.Throws<InvalidDataException>(() => ModInstall.Inspect(_env, bogus));
    }

    // -------------------------------------------------------------- overwrite ---

    [Fact]
    public void ExistingMod_SkippedWithoutOverwrite_ReplacedWithIt()
    {
        var v1 = Zip(
            ("M/mod_info.json", """[{"strName":"OverMod"}]"""),
            ("M/data/a.json", "v1"),
            ("M/data/stale.json", "old"));
        ModInstall.Execute(_env, ModInstall.Inspect(_env, v1));
        Assert.Equal("v1", File.ReadAllText(Mods("OverMod", "data", "a.json")));

        var v2 = Zip(
            ("M/mod_info.json", """[{"strName":"OverMod"}]"""),
            ("M/data/a.json", "v2"));

        // without overwrite: the collision is skipped, nothing changes
        var plan2 = ModInstall.Inspect(_env, v2);
        Assert.True(Assert.Single(plan2.Components).Exists);
        var skipResult = ModInstall.Execute(_env, plan2, overwrite: false);
        Assert.Empty(skipResult.Installed);
        Assert.Single(skipResult.Skipped);
        Assert.Equal("v1", File.ReadAllText(Mods("OverMod", "data", "a.json")));

        // with overwrite: clean replace (stale file from v1 is gone)
        var overResult = ModInstall.Execute(_env, plan2, overwrite: true);
        Assert.Single(overResult.Installed);
        Assert.Equal("v2", File.ReadAllText(Mods("OverMod", "data", "a.json")));
        Assert.False(File.Exists(Mods("OverMod", "data", "stale.json")));
    }

    [Fact]
    public void ReadOnlyExistingMod_OverwriteSucceeds()
    {
        var v1 = Zip(
            ("M/mod_info.json", """[{"strName":"RoMod"}]"""),
            ("M/data/a.json", "v1"));
        ModInstall.Execute(_env, ModInstall.Inspect(_env, v1));
        // mods unzipped from a download often carry ReadOnly on their folders
        new DirectoryInfo(Mods("RoMod")).Attributes |= FileAttributes.ReadOnly;
        new DirectoryInfo(Mods("RoMod", "data")).Attributes |= FileAttributes.ReadOnly;

        var v2 = Zip(
            ("M/mod_info.json", """[{"strName":"RoMod"}]"""),
            ("M/data/a.json", "v2"));
        ModInstall.Execute(_env, ModInstall.Inspect(_env, v2), overwrite: true);
        Assert.Equal("v2", File.ReadAllText(Mods("RoMod", "data", "a.json")));
    }

    // ------------------------------------------------------------ registration ---

    [Fact]
    public void RegisterInstalled_AddsLocalModToLoadOrder()
    {
        var zip = Zip(
            ("MyMod/mod_info.json", """[{"strName":"RegMod"}]"""),
            ("MyMod/data/items/x.json", "[]"));

        var result = ModInstall.Execute(_env, ModInstall.Inspect(_env, zip));
        var registered = ModInstall.RegisterInstalled(_env, result);

        Assert.Equal(new[] { "RegMod|edit" }, registered);
        Assert.Equal(new[] { "core", "RegMod|edit" }, LoadOrderFile.Read(_env.LoadingOrderPath).Order);
    }

    [Fact]
    public void RegisterInstalled_IsIdempotentForAnAlreadyRegisteredMod()
    {
        var zip = Zip(
            ("MyMod/mod_info.json", """[{"strName":"RegMod"}]"""),
            ("MyMod/data/items/x.json", "[]"));

        var result = ModInstall.Execute(_env, ModInstall.Inspect(_env, zip));
        ModInstall.RegisterInstalled(_env, result);
        var again = ModInstall.RegisterInstalled(_env, result);   // second pass: nothing new

        Assert.Empty(again);
        Assert.Equal(new[] { "core", "RegMod|edit" }, LoadOrderFile.Read(_env.LoadingOrderPath).Order);
    }
}
