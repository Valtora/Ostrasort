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
    public void Regenerate_RemovesDataFilesForVanishedConflicts()
    {
        // two conflict classes: the conditions merge (fixture) plus a condowners one
        WriteJson(Path.Combine(_env.ModsDir, "ModA", "data", "condowners", "co.json"),
            """[{"strName":"NewCO","fMass":1}]""");
        WriteJson(Path.Combine(_env.ModsDir, "ModB", "data", "condowners", "co.json"),
            """[{"strName":"NewCO","nCount":5}]""");
        var state = Engine.Analyze(_env);
        Patcher.Generate(_env, Patcher.PlanMerge(_env, state.Analysis), state.Analysis, _env.InstalledVersion, "test");
        var condownersFile = Path.Combine(_env.ModsDir, Patcher.FolderName, "data", "condowners", "ostrasort_merged.json");
        Assert.True(File.Exists(condownersFile));

        // the condowners conflict disappears (ModB drops its version); on
        // regeneration the old merged file must not survive as a ghost override
        File.Delete(Path.Combine(_env.ModsDir, "ModB", "data", "condowners", "co.json"));
        state = Engine.Analyze(_env);
        Patcher.Generate(_env, Patcher.PlanMerge(_env, state.Analysis), state.Analysis, _env.InstalledVersion, "test");

        Assert.False(File.Exists(condownersFile));
        Assert.True(File.Exists(Path.Combine(_env.ModsDir, Patcher.FolderName,
            "data", "conditions", "ostrasort_merged.json")));   // surviving conflict still written
    }

    [Fact]
    public void Generate_RefusesAFolderItDidNotGenerate()
    {
        var dir = Path.Combine(_env.ModsDir, Patcher.FolderName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "somefile.txt"), "not ours");

        var state = Engine.Analyze(_env);
        var plan = Patcher.PlanMerge(_env, state.Analysis);
        Assert.Throws<InvalidOperationException>(() =>
            Patcher.Generate(_env, plan, state.Analysis, _env.InstalledVersion, "test"));
        Assert.True(File.Exists(Path.Combine(dir, "somefile.txt")));   // nothing touched
    }

    [Fact]
    public void Inspect_FlagsPatchRegisteredBeforeASourceMod()
    {
        var state = Engine.Analyze(_env);
        Patcher.Generate(_env, Patcher.PlanMerge(_env, state.Analysis), state.Analysis, _env.InstalledVersion, "test");

        // simulate a wrong order: the patch loads before the mods it merges,
        // so their whole-object overrides beat the merged version
        File.WriteAllText(_env.LoadingOrderPath,
            """[{"strName":"Mod Loading Order","aLoadOrder":["core","OstrasortPatch","ModA","ModB"]}]""");
        state = Engine.Analyze(_env);

        Assert.True(state.Patch.Stale);
        Assert.Contains(state.Patch.StaleReasons, r => r.Contains("loads before"));
        Assert.False(state.Analysis.Collisions.Single(c => c.ObjName == "CondX").ResolvedByPatch);
    }

    [Fact]
    public void IdenticalOverrides_NothingLost_NotFlaggedForAttention()
    {
        WriteJson(Path.Combine(_env.CoreDataDir, "condowners", "co.json"),
            """[{"strName":"SameCO","fMass":1}]""");
        WriteJson(Path.Combine(_env.ModsDir, "ModA", "data", "condowners", "co.json"),
            """[{"strName":"SameCO","fMass":2}]""");
        WriteJson(Path.Combine(_env.ModsDir, "ModB", "data", "condowners", "co.json"),
            """[{"strName":"SameCO","fMass":2}]""");   // identical override

        var state = Engine.Analyze(_env);
        var col = state.Analysis.Collisions.Single(c => c.ObjName == "SameCO");
        Assert.True(col.NothingLost);
        Assert.False(col.ObjectMergeable);
        Assert.False(CollisionView.NeedsAttention(col));
    }

    [Fact]
    public void FfuInstalled_ArrayConflictBetweenPlainMods_StillMergeable()
    {
        // with the FFU framework present, a contested a* field between two
        // NON-FFU mods must still be patchable - the synthetic "__union__"
        // option must not poison the all-options-non-FFU test
        WriteJson(Path.Combine(_env.CoreDataDir, "condowners", "co2.json"),
            """[{"strName":"TagCO","aTags":["x"]}]""");
        WriteJson(Path.Combine(_env.ModsDir, "ModA", "data", "condowners", "co2.json"),
            """[{"strName":"TagCO","aTags":["x","y"]}]""");
        WriteJson(Path.Combine(_env.ModsDir, "ModB", "data", "condowners", "co2.json"),
            """[{"strName":"TagCO","aTags":["x","z"]}]""");

        var state = Engine.Analyze(_env);
        var a = state.Analysis;
        a.Ffu = new FfuContext { FrameworkPresent = true };
        var col = a.Collisions.Single(c => c.ObjName == "TagCO");
        col.ObjectMergeable = false; col.FfuMergedAtLoad = false; col.FieldNotes.Clear();
        col.NothingLost = false;
        FieldDiff.Annotate(_env, a);

        Assert.True(col.ObjectMergeable);
        Assert.False(col.FfuMergedAtLoad);
    }

    [Fact]
    public void LootPool_WithFfuCommandsInAnyArray_IsNotPatchable()
    {
        // ModA's pool carries FFU precision commands in aCOs (aLoots plain);
        // folding it into a union would drop the command edits - the pool must
        // be excluded from the patch and marked merged-at-load instead
        WriteJson(Path.Combine(_env.CoreDataDir, "loot", "pool.json"),
            """[{"strName":"PoolX","aLoots":["ItmA=1.0x1"],"aCOs":[]}]""");
        WriteJson(Path.Combine(_env.ModsDir, "ModA", "data", "loot", "pool.json"),
            """[{"strName":"PoolX","aLoots":["ItmA=1.0x1","ItmB=1.0x2"],"aCOs":["--ADD--","CondY=1.0x1"]}]""");
        WriteJson(Path.Combine(_env.ModsDir, "ModB", "data", "loot", "pool.json"),
            """[{"strName":"PoolX","aLoots":["ItmA=1.0x1","ItmC=1.0x3"],"aCOs":[]}]""");

        var state = Engine.Analyze(_env);
        var col = state.Analysis.Collisions.Single(c => c.ObjName == "PoolX");
        Assert.DoesNotContain(Patcher.PatchableConflicts(state.Analysis), c => c.ObjName == "PoolX");
        Assert.True(col.FfuMergedAtLoad);
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
    public void UndoRestore_RefusesForeignPatchFolder_WithoutTouchingTheLoadOrder()
    {
        var snap = UndoOps.Capture(_env, "test");
        // now corrupt the world: a foreign folder squats on the patch name and
        // the load order changes
        Directory.CreateDirectory(Path.Combine(_env.ModsDir, Patcher.FolderName));
        File.WriteAllText(_env.LoadingOrderPath,
            """[{"strName":"Mod Loading Order","aLoadOrder":["core","ModA"]}]""");
        var before = File.ReadAllText(_env.LoadingOrderPath);

        // validation must run BEFORE any write - a refusal leaves the order file untouched
        Assert.Throws<InvalidOperationException>(() => UndoOps.Restore(_env, snap));
        Assert.Equal(before, File.ReadAllText(_env.LoadingOrderPath));
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
