using System.IO;
using Ostrasort;
using Xunit;

namespace Ostrasort.Tests;

public class ModRegistrationTests : IDisposable
{
    private readonly string _root;
    private readonly GameEnv _env;

    public ModRegistrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "OstraReg_" + Guid.NewGuid().ToString("N"));
        var mods = Path.Combine(_root, "Mods");
        Directory.CreateDirectory(mods);
        _env = new GameEnv
        {
            GameRoot = _root, DiscoveredVia = "test",
            CoreDataDir = Path.Combine(_root, "core"), ModsDir = mods,
        };
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void WriteOrder(params string[] raws)
    {
        var arr = string.Join(",", raws.Select(r => "\"" + r.Replace("\\", "\\\\") + "\""));
        File.WriteAllText(_env.LoadingOrderPath,
            "[{\"strName\":\"Mod Loading Order\",\"aLoadOrder\":[" + arr + "]}]");
    }

    private static Analysis WithRegistered(params ModEntry[] registered) =>
        new() { Registered = registered.ToList() };

    private ModEntry Core() => ModEntry.Parse("core", _env);

    private static ModEntry UnregisteredLocal(string name) =>
        new() { Raw = "", Kind = EntryKind.Local, Name = name, Registered = false };

    [Fact]
    public void Register_AppendsLocalWithEditMarker_WhenNoAnchors()
    {
        WriteOrder("core");
        var result = ModRegistration.Register(_env, UnregisteredLocal("NewMod"), WithRegistered(Core()));

        Assert.False(result.AlreadyRegistered);
        Assert.Equal("NewMod|edit", result.Entry);
        Assert.Equal(1, result.Position);
        Assert.Equal(new[] { "core", "NewMod|edit" }, LoadOrderFile.Read(_env.LoadingOrderPath).Order);
    }

    [Fact]
    public void Register_InsertsBeforeFfuBlock()
    {
        WriteOrder("core", "FfuMod|edit");
        var ffu = new ModEntry { Raw = "FfuMod|edit", Kind = EntryKind.Local, Name = "FfuMod", IsFfu = true };

        var result = ModRegistration.Register(_env, UnregisteredLocal("NewMod"), WithRegistered(Core(), ffu));

        Assert.Equal(1, result.Position);
        Assert.Equal(new[] { "core", "NewMod|edit", "FfuMod|edit" },
            LoadOrderFile.Read(_env.LoadingOrderPath).Order);
    }

    [Fact]
    public void Register_AddsWorkshopByAbsolutePath()
    {
        WriteOrder("core");
        var ws = new ModEntry
        {
            Raw = "", Kind = EntryKind.Workshop, Name = "123456",
            Dir = @"C:\ws\123456", Registered = false,
        };

        var result = ModRegistration.Register(_env, ws, WithRegistered(Core()));

        Assert.Equal(@"C:\ws\123456", result.Entry);
        Assert.Equal(new[] { "core", @"C:\ws\123456" }, LoadOrderFile.Read(_env.LoadingOrderPath).Order);
    }

    [Fact]
    public void Register_AlreadyListed_IsNoOp()
    {
        WriteOrder("core", "NewMod|edit");
        var result = ModRegistration.Register(_env, UnregisteredLocal("NewMod"), WithRegistered(Core()));

        Assert.True(result.AlreadyRegistered);
        Assert.Equal("NewMod|edit", result.Entry);
        Assert.Equal(new[] { "core", "NewMod|edit" }, LoadOrderFile.Read(_env.LoadingOrderPath).Order);
    }

    [Fact]
    public void Register_RefusesRegisteredCoreAndPatch()
    {
        WriteOrder("core");
        var a = WithRegistered(Core());

        // an already-registered mod
        var reg = new ModEntry { Raw = "X|edit", Kind = EntryKind.Local, Name = "X", Registered = true };
        Assert.Throws<InvalidOperationException>(() => ModRegistration.Register(_env, reg, a));

        // core is base game data, never a registerable mod
        var core = new ModEntry { Raw = "", Kind = EntryKind.Core, Name = "core", Registered = false };
        Assert.Throws<InvalidOperationException>(() => ModRegistration.Register(_env, core, a));

        // the generated patch registers itself via the Patch controls
        var patchDir = Path.Combine(_env.ModsDir, Patcher.FolderName);
        Directory.CreateDirectory(patchDir);
        File.WriteAllText(Path.Combine(patchDir, Patcher.MarkerFile), "{}");
        var patch = new ModEntry
        {
            Raw = "", Kind = EntryKind.Local, Name = Patcher.FolderName, Dir = patchDir, Registered = false,
        };
        Assert.Throws<InvalidOperationException>(() => ModRegistration.Register(_env, patch, a));

        // nothing was written on any refusal
        Assert.Equal(new[] { "core" }, LoadOrderFile.Read(_env.LoadingOrderPath).Order);
    }
}
