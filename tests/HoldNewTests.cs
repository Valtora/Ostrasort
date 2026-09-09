using System.IO;
using Ostrasort;
using Xunit;

namespace Ostrasort.Tests;

/// <summary>
/// "Add new mods turned off": a mod found on disk but missing from aLoadOrder is
/// registered with the game's own |disabled marker instead of loading, so the
/// player ticks it Active when ready. Against a real on-disk install, because the
/// behaviour depends on discovery, scanning and the entry strings actually written.
/// </summary>
public class HoldNewTests : IDisposable
{
    private readonly string _root;
    private readonly string _mods;
    private readonly string _workshop;
    private readonly GameEnv _env;
    private readonly IgnoreList _ignore;
    private readonly string _ignorePath;

    public HoldNewTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "OstraHold_" + Guid.NewGuid().ToString("N"));
        var core = Path.Combine(_root, "core");
        _mods = Path.Combine(_root, "Mods");
        _workshop = Path.Combine(_root, "workshop");
        Directory.CreateDirectory(Path.Combine(core, "loot"));
        File.WriteAllText(Path.Combine(core, "loot", "p.json"),
            """[{"strName":"PoolP","aLoots":["x=1x1"]}]""");

        Mod(Path.Combine(_mods, "Registered"), "Registered");
        Mod(Path.Combine(_mods, "NewLocal"), "New Local");            // on disk, not in the order
        Mod(Path.Combine(_workshop, "9001"), "New Workshop");         // subscribed, not in the order
        File.WriteAllText(Path.Combine(_mods, "loading_order.json"),
            """[{"strName":"Mod Loading Order","aLoadOrder":["core","Registered|edit"]}]""");

        _env = new GameEnv
        {
            GameRoot = _root, DiscoveredVia = "test",
            CoreDataDir = core, ModsDir = _mods, WorkshopContentDir = _workshop,
            InstalledVersion = "0.0.0.1",
        };
        _ignorePath = Path.Combine(Path.GetTempPath(), "OstraHoldIgn_" + Guid.NewGuid().ToString("N") + ".json");
        _ignore = new IgnoreList(_ignorePath);
    }

    /// <summary>A minimal mod folder: mod_info.json plus one data object of its own.</summary>
    private static void Mod(string dir, string name, string? patcherDll = null)
    {
        Directory.CreateDirectory(Path.Combine(dir, "data", "loot"));
        File.WriteAllText(Path.Combine(dir, "mod_info.json"), $$"""{"strName":"{{name}}"}""");
        File.WriteAllText(Path.Combine(dir, "data", "loot", "own.json"),
            $$"""[{"strName":"Pool{{name.Replace(" ", "")}}","aLoots":["y=1x1"]}]""");
        if (patcherDll is not null)
        {
            Directory.CreateDirectory(Path.Combine(dir, "BepInEx", "patchers"));
            File.WriteAllText(Path.Combine(dir, "BepInEx", "patchers", patcherDll), "not really a dll");
        }
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        if (File.Exists(_ignorePath)) File.Delete(_ignorePath);
    }

    private EngineState Analyze(bool holdNew) =>
        Engine.Analyze(_env, ignore: _ignore, holdNew: holdNew);

    [Fact]
    public void Off_ByDefault_NewModsAreAddedLoading()
    {
        var a = Analyze(holdNew: false).Analysis;

        Assert.Contains("NewLocal|edit", a.SuggestedOrder);
        Assert.Contains(Path.Combine(_workshop, "9001"), a.SuggestedOrder);
        Assert.DoesNotContain(a.SuggestedOrder, e => e.EndsWith("|disabled"));
    }

    [Fact]
    public void On_NewLocalAndWorkshopMods_AreRegisteredTurnedOff()
    {
        var a = Analyze(holdNew: true).Analysis;

        // the game's own 2-part marker: |disabled REPLACES |edit, it never joins it
        Assert.Contains("NewLocal|disabled", a.SuggestedOrder);
        Assert.DoesNotContain("NewLocal|edit", a.SuggestedOrder);
        // a Workshop entry is an absolute path, not the item id
        Assert.Contains(Path.Combine(_workshop, "9001") + "|disabled", a.SuggestedOrder);
        Assert.Contains(a.Changes, c => c.Action == "add" && c.Reason.Contains("tick Active to load it"));
    }

    [Fact]
    public void On_TheEntryIsStillPlacedByTheOrderingRules()
    {
        // holding is about loading, not about membership: core stays first and the
        // held mods still take a considered slot, so the game stops re-adding them
        var a = Analyze(holdNew: true).Analysis;

        Assert.Equal("core", a.SuggestedOrder[0]);
        Assert.Equal(4, a.SuggestedOrder.Count);   // core + Registered + the two held
    }

    [Fact]
    public void On_HeldModRaisesNoConflictsUntilItIsTurnedOn()
    {
        // NewLocal and the Workshop mod both claim PoolP, so unheld they collide
        File.WriteAllText(Path.Combine(_mods, "NewLocal", "data", "loot", "own.json"),
            """[{"strName":"PoolP","aLoots":["x=1x1","a=1x1"]}]""");
        File.WriteAllText(Path.Combine(_workshop, "9001", "data", "loot", "own.json"),
            """[{"strName":"PoolP","aLoots":["x=1x1","b=1x1"]}]""");

        Assert.NotEmpty(Analyze(holdNew: false).Analysis.Collisions);
        Assert.Empty(Analyze(holdNew: true).Analysis.Collisions);
    }

    [Fact]
    public void On_ApplyingTheSuggestionTwiceChangesNothingTheSecondTime()
    {
        // the churn regression: SuggestedRaw and the discovery pass must agree on
        // the entry string, or every rescan would re-propose the same write
        var first = Analyze(holdNew: true);
        LoadOrderFile.Read(_env.LoadingOrderPath).Write(first.Analysis.SuggestedOrder);

        var second = Analyze(holdNew: true).Analysis;

        Assert.False(second.OrderChanged);
        Assert.Empty(second.Changes);
    }

    [Fact]
    public void On_TheGeneratedPatchIsNeverHeld()
    {
        // the patch IS the resolution of a conflict - holding it would leave the
        // conflict unfixed with no sign of why
        var patchDir = Path.Combine(_mods, "OstrasortPatch");
        Mod(patchDir, "Ostrasort Patch");
        File.WriteAllText(Path.Combine(patchDir, Patcher.MarkerFile), "{}");

        var a = Analyze(holdNew: true).Analysis;

        Assert.Contains("OstrasortPatch", a.SuggestedOrder);
        Assert.DoesNotContain("OstrasortPatch|disabled", a.SuggestedOrder);
    }

    [Fact]
    public void On_TheModLoaderIsNeverHeld()
    {
        // holding infrastructure breaks every code mod rather than deferring one
        Mod(Path.Combine(_mods, "ModLoader"), "Mod Loader", patcherDll: "loader.dll");

        var a = Analyze(holdNew: true).Analysis;

        Assert.Contains("ModLoader|edit", a.SuggestedOrder);
        Assert.DoesNotContain("ModLoader|disabled", a.SuggestedOrder);
    }

    [Fact]
    public void On_AnIgnoredLocalModIsStillLeftOutEntirely()
    {
        // "Ignore (leave unregistered)" is a stronger statement than holding, and
        // the two must not fight: an ignored folder never enters the order at all
        _ignore.Add($"{_env.ModsDir}|NewLocal");

        var a = Analyze(holdNew: true).Analysis;

        Assert.DoesNotContain(a.SuggestedOrder, e => e.StartsWith("NewLocal"));
    }
}
