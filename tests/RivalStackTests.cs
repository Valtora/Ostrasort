using System.IO;
using Ostrasort;
using Xunit;

namespace Ostrasort.Tests;

public class RivalStackTests
{
    private static GameEnv EnvAt(string root) => new()
    {
        GameRoot = root,
        DiscoveredVia = "test",
        CoreDataDir = Path.Combine(root, "Ostranauts_Data", "StreamingAssets", "data"),
        ModsDir = Path.Combine(root, "Ostranauts_Data", "Mods"),
    };

    private static string NewTempRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ostrasort-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
    }

    private static void WithRoot(Action<string> body)
    {
        var root = NewTempRoot();
        try { body(root); }
        finally { try { Directory.Delete(root, true); } catch { /* best-effort cleanup */ } }
    }

    [Fact]
    public void Detect_NullWhenNoBepInEx() =>
        WithRoot(root => Assert.Null(RivalStack.Detect(EnvAt(root))));

    [Fact]
    public void Detect_NullForOrdinaryWorkshopPlugin() => WithRoot(root =>
    {
        Touch(Path.Combine(root, "BepInEx", "plugins", "SomeMod", "SomeMod.dll"));
        Assert.Null(RivalStack.Detect(EnvAt(root)));
    });

    [Fact]
    public void Detect_FfuMonoModPatch() => WithRoot(root =>
    {
        Touch(Path.Combine(root, "BepInEx", "monomod", "Assembly-CSharp.FFU_BR.mm.dll"));
        var r = RivalStack.Detect(EnvAt(root));
        Assert.NotNull(r);
        Assert.True(r!.Ffu);
        Assert.True(r.MonoMod);
    });

    [Fact]
    public void Detect_AutoloaderMetaFile() => WithRoot(root =>
    {
        Touch(Path.Combine(root, "BepInEx", "plugins", "Minor_Fixes_Plus", "Autoload.Meta.toml"));
        var r = RivalStack.Detect(EnvAt(root));
        Assert.NotNull(r);
        Assert.True(r!.Autoloader);
    });

    [Fact]
    public void Detect_AutoloaderPluginDll() => WithRoot(root =>
    {
        Touch(Path.Combine(root, "BepInEx", "plugins", "OstraAutoloader", "Ostra.Autoloader.dll"));
        var r = RivalStack.Detect(EnvAt(root));
        Assert.NotNull(r);
        Assert.True(r!.Autoloader);
    });

    [Fact]
    public void Detect_MetaInModsFolder() => WithRoot(root =>
    {
        Touch(Path.Combine(root, "BepInEx", "core", "BepInEx.dll"));                       // BepInEx present but clean
        Touch(Path.Combine(root, "Ostranauts_Data", "Mods", "SomeFfuMod", "Autoload.Meta.toml"));
        var r = RivalStack.Detect(EnvAt(root));
        Assert.NotNull(r);
        Assert.True(r!.Autoloader);
    });
}
