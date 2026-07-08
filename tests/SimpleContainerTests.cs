using System.IO;
using Ostrasort;
using Xunit;

namespace Ostrasort.Tests;

/// <summary>
/// Regression guard for the conditions_simple crash: flat-packed "JsonSimple"
/// container types (conditions_simple, strings, names_*, …) are exploded into
/// individual records by the game AFTER every mod loads, never whole-object-
/// replaced. So two mods each shipping their own container must NOT be treated
/// as a mergeable whole-object collision - unioning the fixed-width aValues
/// corrupts the packing and crashes the game on load. They are report-only.
/// </summary>
public class SimpleContainerTests : IDisposable
{
    private readonly string _root;
    private readonly GameEnv _env;

    public SimpleContainerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "OstraSimple_" + Guid.NewGuid().ToString("N"));
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

        // core ships the canonical container (as the real game does)
        WriteJson(Path.Combine(core, "conditions_simple", "conditions_simple.json"),
            """[{"strName":"Simple Conditions","aValues":["CoreCond","Core","desc","0","0","Neutral","false"]}]""");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static void WriteJson(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    private void Order(params string[] mods) =>
        File.WriteAllText(Path.Combine(_env.ModsDir, "loading_order.json"),
            $$"""[{"strName":"Mod Loading Order","aLoadOrder":["core",{{string.Join(",", mods.Select(m => $"\"{m}\""))}}]}]""");

    [Fact]
    public void TwoMods_EachAddDifferentSimpleConditions_IsAdditiveNotMergeable()
    {
        // each mod ships its OWN "Simple Conditions" container with different conditions
        WriteJson(Path.Combine(_env.ModsDir, "ModA", "data", "conditions_simple", "mine.json"),
            """[{"strName":"Simple Conditions","aValues":["CondA","Alpha","desc","0","0","Neutral","false"]}]""");
        WriteJson(Path.Combine(_env.ModsDir, "ModB", "data", "conditions_simple", "mine.json"),
            """[{"strName":"Simple Conditions","aValues":["CondB","Beta","desc","0","0","Neutral","false"]}]""");
        Order("ModA", "ModB");

        var state = Engine.Analyze(_env);

        var col = Assert.Single(state.Analysis.Collisions,
            c => c is { Type: "conditions_simple", ObjName: "Simple Conditions" });
        Assert.True(col.AdditiveAtLoad);
        Assert.False(col.ObjectMergeable);
        Assert.DoesNotContain(col, Patcher.MergeableObjects(state.Analysis));
        Assert.Contains(col.FieldNotes, n => n.Contains("one-by-one at load"));

        // nothing to patch -> no OstrasortPatch generated (so no crashing container)
        Assert.False(Patcher.HasWork(state.Analysis));
        Assert.Empty(Patcher.PlanMerge(_env, state.Analysis).Objects);
    }

    [Fact]
    public void TwoMods_SameSimpleCondition_StillDetectedInConditionsNamespace()
    {
        // both define the SAME inner condition -> a real per-record collision,
        // reported under the conditions namespace (the game's last-loaded wins)
        WriteJson(Path.Combine(_env.ModsDir, "ModA", "data", "conditions_simple", "mine.json"),
            """[{"strName":"Simple Conditions","aValues":["CondShared","FromA","desc","0","0","Neutral","false"]}]""");
        WriteJson(Path.Combine(_env.ModsDir, "ModB", "data", "conditions_simple", "mine.json"),
            """[{"strName":"Simple Conditions","aValues":["CondShared","FromB","desc","0","0","Neutral","false"]}]""");
        Order("ModA", "ModB");

        var state = Engine.Analyze(_env);

        Assert.Contains(state.Analysis.Collisions,
            c => c is { Type: "conditions", ObjName: "CondShared" });
        // and it is never a crashing object-merge either
        Assert.Empty(Patcher.MergeableObjects(state.Analysis));
    }

    [Fact]
    public void TwoMods_EachShipStringsContainer_IsAdditiveNotMergeable()
    {
        WriteJson(Path.Combine(_env.ModsDir, "ModA", "data", "strings", "s.json"),
            """[{"strName":"Strings","aValues":["KeyA","Value A"]}]""");
        WriteJson(Path.Combine(_env.ModsDir, "ModB", "data", "strings", "s.json"),
            """[{"strName":"Strings","aValues":["KeyB","Value B"]}]""");
        Order("ModA", "ModB");

        var state = Engine.Analyze(_env);

        var col = Assert.Single(state.Analysis.Collisions, c => c.Type == "strings");
        Assert.True(col.AdditiveAtLoad);
        Assert.False(col.ObjectMergeable);
        Assert.Empty(Patcher.MergeableObjects(state.Analysis));
    }
}
