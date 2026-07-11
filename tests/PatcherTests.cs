using System.IO;
using Ostrasort;
using Xunit;

namespace Ostrasort.Tests;

/// <summary>
/// End-to-end patch lifecycle against a synthetic install: two mods change
/// different fields of one core object, the patch merges them, and the
/// staleness checks catch both a mod-side and a CORE-side (game update)
/// change afterwards.
/// </summary>
public class PatcherTests : IDisposable
{
    private readonly string _root;
    private readonly GameEnv _env;

    public PatcherTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "OstraPatch_" + Guid.NewGuid().ToString("N"));
        var core = Path.Combine(_root, "core");
        var mods = Path.Combine(_root, "Mods");
        Directory.CreateDirectory(core);
        Directory.CreateDirectory(mods);
        _env = new GameEnv
        {
            GameRoot = _root,
            DiscoveredVia = "test",
            CoreDataDir = core,
            ModsDir = mods,
            InstalledVersion = "0.0.0.1",
        };

        WriteJson(Path.Combine(core, "conditions", "cond.json"),
            """[{"strName":"CondX","fSev":1,"strColor":"Red"}]""");
        WriteJson(Path.Combine(mods, "ModA", "data", "conditions", "cond.json"),
            """[{"strName":"CondX","fSev":2,"strColor":"Red"}]""");     // changes fSev only
        WriteJson(Path.Combine(mods, "ModB", "data", "conditions", "cond.json"),
            """[{"strName":"CondX","fSev":1,"strColor":"Blue"}]""");    // changes strColor only
        File.WriteAllText(Path.Combine(mods, "loading_order.json"),
            """[{"strName":"Mod Loading Order","aLoadOrder":["core","ModA","ModB"]}]""");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static void WriteJson(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    [Fact]
    public void PatchLifecycle_FreshThenStaleWhenCoreChanges()
    {
        // disjoint field edits on a core object -> mergeable, nothing contested
        var state = Engine.Analyze(_env);
        var col = Assert.Single(state.Analysis.Collisions);
        Assert.True(col.ObjectMergeable);

        var plan = Patcher.PlanMerge(_env, state.Analysis);
        Assert.Empty(plan.Unresolved);   // disjoint edits auto-merge
        Patcher.Generate(_env, plan, state.Analysis, _env.InstalledVersion, "test");

        // fresh: the patch covers the collision and registers itself
        state = Engine.Analyze(_env);
        Assert.False(state.Patch.Stale);
        Assert.Contains("conditions/CondX", state.Patch.CoveredKeys);
        Assert.Contains(Patcher.FolderName, state.Lo.Order);

        // the merged object took ModA's fSev and ModB's strColor
        var merged = File.ReadAllText(Path.Combine(_env.ModsDir, Patcher.FolderName,
            "data", "conditions", "ostrasort_merged.json"));
        Assert.Contains("\"fSev\": 2", merged);
        Assert.Contains("\"strColor\": \"Blue\"", merged);

        // a GAME UPDATE changes the core base -> the merge overlays a stale
        // vanilla object and must be flagged even though no mod changed
        WriteJson(Path.Combine(_env.CoreDataDir, "conditions", "cond.json"),
            """[{"strName":"CondX","fSev":99,"strColor":"Red","strNew":"added by update"}]""");
        state = Engine.Analyze(_env);
        Assert.True(state.Patch.Stale);
        Assert.Contains(state.Patch.StaleReasons, r => r.Contains("BASE GAME"));
    }

    [Fact]
    public void ModAddedObject_NoCoreAncestor_TwoWayMerges()
    {
        // two mods each ADD the same new object (no vanilla version). Disjoint
        // fields must two-way merge instead of the game dropping ModA's version.
        WriteJson(Path.Combine(_env.ModsDir, "ModA", "data", "condowners", "co.json"),
            """[{"strName":"NewCO","strDesc":"from A","fMass":1}]""");
        WriteJson(Path.Combine(_env.ModsDir, "ModB", "data", "condowners", "co.json"),
            """[{"strName":"NewCO","strDesc":"from A","nCount":5}]""");   // agrees on strDesc, adds nCount
        File.WriteAllText(_env.LoadingOrderPath,
            """[{"strName":"Mod Loading Order","aLoadOrder":["core","ModA","ModB"]}]""");

        var state = Engine.Analyze(_env);
        var col = state.Analysis.Collisions.Single(c => c.ObjName == "NewCO");
        Assert.True(col.ObjectMergeable);

        var plan = Patcher.PlanMerge(_env, state.Analysis);
        Assert.Empty(plan.Unresolved);   // disjoint additions auto-merge
        Patcher.Generate(_env, plan, state.Analysis, _env.InstalledVersion, "test");

        var merged = File.ReadAllText(Path.Combine(_env.ModsDir, Patcher.FolderName,
            "data", "condowners", "ostrasort_merged.json"));
        Assert.Contains("\"fMass\": 1", merged);      // only ModA set it - kept
        Assert.Contains("\"nCount\": 5", merged);     // only ModB set it - kept

        // a mod-added merge has no core base, so a mod-side change makes it stale
        // but a (non-existent) core change never can
        state = Engine.Analyze(_env);
        Assert.False(state.Patch.Stale);
        Assert.Contains("condowners/NewCO", state.Patch.CoveredKeys);
    }

    [Fact]
    public void Remove_DeletesFolderAndDropsLoadOrderEntry()
    {
        var state = Engine.Analyze(_env);
        Patcher.Generate(_env, Patcher.PlanMerge(_env, state.Analysis), state.Analysis, _env.InstalledVersion, "test");
        var dir = Path.Combine(_env.ModsDir, Patcher.FolderName);
        Assert.True(Directory.Exists(dir));
        Assert.Contains(Patcher.FolderName, LoadOrderFile.Read(_env.LoadingOrderPath).Order);

        Patcher.Remove(_env);   // what the GUI's mod-table "Remove" and the Patch tab both call

        Assert.False(Directory.Exists(dir));
        Assert.DoesNotContain(Patcher.FolderName, LoadOrderFile.Read(_env.LoadingOrderPath).Order);
    }

    [Fact]
    public void Remove_RefusesAFolderItDidNotGenerate()
    {
        // a folder named like the patch but WITHOUT the marker must not be deleted
        var dir = Path.Combine(_env.ModsDir, Patcher.FolderName);
        Directory.CreateDirectory(dir);
        Assert.Throws<InvalidOperationException>(() => Patcher.Remove(_env));
        Assert.True(Directory.Exists(dir));
    }

    [Fact]
    public void PatchLifecycle_StaleWhenASourceModChanges()
    {
        var state = Engine.Analyze(_env);
        var plan = Patcher.PlanMerge(_env, state.Analysis);
        Patcher.Generate(_env, plan, state.Analysis, _env.InstalledVersion, "test");

        WriteJson(Path.Combine(_env.ModsDir, "ModA", "data", "conditions", "cond.json"),
            """[{"strName":"CondX","fSev":7,"strColor":"Red"}]""");
        state = Engine.Analyze(_env);
        Assert.True(state.Patch.Stale);
        Assert.Contains(state.Patch.StaleReasons, r => r.Contains("source mod"));
    }
}
