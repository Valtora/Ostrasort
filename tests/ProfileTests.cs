using System.IO;
using Ostrasort;
using Xunit;

namespace Ostrasort.Tests;

/// <summary>
/// Profiles against a synthetic install: the store round-trips per install, and
/// the switch planner honours Replace / Merge-append, filters missing mods,
/// de-dups marker variants, never carries the generated patch, and keeps core
/// first. The last test exercises a real switch through the guarded write.
/// </summary>
public class ProfileTests : IDisposable
{
    private readonly string _root;
    private readonly GameEnv _env;

    public ProfileTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "OstraProf_" + Guid.NewGuid().ToString("N"));
        var core = Path.Combine(_root, "core");
        var mods = Path.Combine(_root, "Mods");
        Directory.CreateDirectory(core);
        foreach (var name in new[] { "ModA", "ModB", "ModC" })
        {
            Directory.CreateDirectory(Path.Combine(mods, name));
            File.WriteAllText(Path.Combine(mods, name, "mod_info.json"), $$"""[{"strName":"{{name}}"}]""");
        }
        File.WriteAllText(Path.Combine(mods, "loading_order.json"),
            """[{"strName":"Mod Loading Order","aLoadOrder":["core","ModA|edit","ModB|edit"]}]""");
        _env = new GameEnv
        {
            GameRoot = _root, DiscoveredVia = "test",
            CoreDataDir = core, ModsDir = mods, InstalledVersion = "0.15.1.6",
        };
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        foreach (var lo in new[] { _env.LoadingOrderPath, Path.Combine(_root, "Other", "loading_order.json") })
            try { Directory.Delete(ProfileStore.DirFor(lo), recursive: true); } catch { }
    }

    private string Lo => _env.LoadingOrderPath;
    private Analysis Registered(params string[] raws) =>
        new() { Registered = raws.Select(r => ModEntry.Parse(r, _env)).ToList() };
    private static Profile Named(string name, params string[] raws) =>
        new() { Name = name, Entries = raws.Select(r => new ProfileEntry(r, r.Split('|')[0])).ToList() };

    // ------------------------------------------------------------- store ---

    [Fact]
    public void Store_RoundTripsAndOverwritesByName()
    {
        var profile = Profile.Capture(Registered("core", "ModA|edit", "ModB|edit"), "My Setup", "0.15.1.6", "2026-07-05T10:00:00");
        ProfileStore.Save(Lo, profile);

        Assert.True(ProfileStore.Exists(Lo, "my setup"));   // identity is case-insensitive
        var loaded = ProfileStore.Load(Lo, "My Setup")!;
        Assert.Equal(new[] { "core", "ModA|edit", "ModB|edit" }, loaded.Raws);
        Assert.Equal("0.15.1.6", loaded.SavedGameVersion);
        Assert.Equal(2, loaded.ModCount);

        // same name overwrites in place - never a second file
        ProfileStore.Save(Lo, Profile.Capture(Registered("core", "ModA|edit"), "My Setup", null, null));
        Assert.Single(ProfileStore.List(Lo));
        Assert.Equal(new[] { "core", "ModA|edit" }, ProfileStore.Load(Lo, "My Setup")!.Raws);

        ProfileStore.Delete(Lo, "My Setup");
        Assert.Empty(ProfileStore.List(Lo));
    }

    [Fact]
    public void Store_IsPerInstall()
    {
        ProfileStore.Save(Lo, Named("A", "core", "ModA|edit"));
        Assert.Single(ProfileStore.List(Lo));
        Assert.Empty(ProfileStore.List(Path.Combine(_root, "Other", "loading_order.json")));   // different key
    }

    [Fact]
    public void Store_Renames()
    {
        ProfileStore.Save(Lo, Named("Old", "core", "ModA|edit"));
        ProfileStore.Rename(Lo, "Old", "New");
        Assert.Null(ProfileStore.Load(Lo, "Old"));
        Assert.Equal(new[] { "core", "ModA|edit" }, ProfileStore.Load(Lo, "New")!.Raws);
    }

    [Fact]
    public void Capture_ExcludesTheGeneratedPatch()
    {
        var patchDir = Path.Combine(_env.ModsDir, Patcher.FolderName);
        Directory.CreateDirectory(patchDir);
        File.WriteAllText(Path.Combine(patchDir, Patcher.MarkerFile), "{}");

        var profile = Profile.Capture(Registered("core", "ModA|edit", Patcher.FolderName), "p", null, null);
        Assert.Equal(new[] { "core", "ModA|edit" }, profile.Raws);   // the patch entry is dropped
    }

    // ------------------------------------------------------------ switch ---

    [Fact]
    public void Plan_Replace_UsesProfileOrderAndDropsTheRest()
    {
        var current = Registered("core", "ModA|edit", "ModC|edit");
        var plan = ProfileSwitch.Plan(_env, current, Named("s", "core", "ModB|edit"), SwitchMode.Replace);

        Assert.Equal(new[] { "core", "ModB|edit" }, plan.NewOrder);
        Assert.Equal(new[] { "ModA|edit", "ModC|edit" }, plan.Dropped);
        Assert.Empty(plan.Appended);
        Assert.Empty(plan.Missing);
    }

    [Fact]
    public void Plan_Merge_AppendsCurrentModsNotInTheProfile()
    {
        var current = Registered("core", "ModA|edit", "ModC|edit");
        var plan = ProfileSwitch.Plan(_env, current, Named("s", "core", "ModB|edit"), SwitchMode.Merge);

        Assert.Equal(new[] { "core", "ModB|edit", "ModA|edit", "ModC|edit" }, plan.NewOrder);
        Assert.Equal(new[] { "ModA|edit", "ModC|edit" }, plan.Appended);
        Assert.Empty(plan.Dropped);
    }

    [Fact]
    public void Plan_MissingProfileMods_AreSkippedAndReported()
    {
        var plan = ProfileSwitch.Plan(_env, Registered("core"), Named("s", "core", "ModA|edit", "Ghost|edit"), SwitchMode.Replace);

        Assert.Equal(new[] { "core", "ModA|edit" }, plan.NewOrder);
        var missing = Assert.Single(plan.Missing);
        Assert.Equal("Ghost|edit", missing.Raw);
    }

    [Fact]
    public void Plan_DedupsMarkerVariantsAndKeepsMarkers()
    {
        var plan = ProfileSwitch.Plan(_env, Registered("core"),
            Named("s", "core", "ModA|edit", "ModA|disabled", "ModB|disabled"), SwitchMode.Replace);

        // one ModA (first wins), and the |disabled marker survives verbatim
        Assert.Equal(new[] { "core", "ModA|edit", "ModB|disabled" }, plan.NewOrder);
    }

    [Fact]
    public void Plan_CoreIsAlwaysFirst()
    {
        var plan = ProfileSwitch.Plan(_env, Registered("core"), Named("s", "ModA|edit", "core"), SwitchMode.Replace);
        Assert.Equal(new[] { "core", "ModA|edit" }, plan.NewOrder);
    }

    [Fact]
    public void Plan_NeverCarriesThePatch()
    {
        var patchDir = Path.Combine(_env.ModsDir, Patcher.FolderName);
        Directory.CreateDirectory(patchDir);
        File.WriteAllText(Path.Combine(patchDir, Patcher.MarkerFile), "{}");

        // even a profile that somehow lists the patch, and merge over a current patch entry, never carry it
        var current = Registered("core", "ModA|edit", Patcher.FolderName);
        var plan = ProfileSwitch.Plan(_env, current, Named("s", "core", "ModB|edit", Patcher.FolderName), SwitchMode.Merge);

        Assert.DoesNotContain(Patcher.FolderName, plan.NewOrder);
        Assert.Equal(new[] { "core", "ModB|edit", "ModA|edit" }, plan.NewOrder);
    }

    [Fact]
    public void Switch_WritesTheOrderThroughTheGuardedRitual()
    {
        var state = Engine.Analyze(_env);
        var plan = ProfileSwitch.Plan(_env, state.Analysis, Named("s", "core", "ModB|edit"), SwitchMode.Replace);
        LoadOrderFile.Read(Lo).Write(plan.NewOrder);

        Assert.Equal(new[] { "core", "ModB|edit" }, LoadOrderFile.Read(Lo).Order);
        Assert.True(File.Exists(Lo + ".bak"));
        Assert.Contains("ModA|edit", File.ReadAllText(Lo + ".bak"));   // the pre-switch order is preserved
    }
}
