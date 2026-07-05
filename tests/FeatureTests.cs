using System.IO;
using System.Text.Json.Nodes;
using Ostrasort;
using Xunit;
using static Ostrasort.Tests.TestData;

namespace Ostrasort.Tests;

public class BackupsTests
{
    [Fact]
    public void Snapshot_KeepsOnlyThreeNewest()
    {
        var lo = Path.Combine(Path.GetTempPath(), "OstraBak_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            for (var i = 1; i <= 5; i++)
                Backups.Snapshot(lo, $"[\"content {i}\"]");

            var list = Backups.List(lo);
            Assert.Equal(Backups.Keep, list.Count);
            Assert.Equal("[\"content 5\"]", File.ReadAllText(list[0].Path));   // newest first
            Assert.Equal("[\"content 3\"]", File.ReadAllText(list[^1].Path));  // 1 and 2 pruned
        }
        finally
        {
            // clean the per-path backup dir out of LOCALAPPDATA
            if (Backups.List(lo) is { Count: > 0 } left)
                Directory.Delete(Path.GetDirectoryName(left[0].Path)!, recursive: true);
        }
    }
}

public class IgnoreListTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "OstraIgn_" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void AddRemove_PersistsAcrossLoads()
    {
        var list = new IgnoreList(_path);
        list.Add(@"C:\game\Mods|OldMod");
        Assert.True(new IgnoreList(_path).Contains(@"C:\game\Mods|OldMod"));
        Assert.True(new IgnoreList(_path).Contains(@"c:\game\mods|oldmod"));   // case-insensitive

        list.Remove(@"C:\game\Mods|OldMod");
        Assert.False(new IgnoreList(_path).Contains(@"C:\game\Mods|OldMod"));
    }
}

public class IgnoredModTests
{
    [Fact]
    public void Suggestion_SkipsIgnoredUnregisteredMods()
    {
        var core = new ModEntry { Raw = "core", Kind = EntryKind.Core, Name = "core", Dir = "core", Class = ModClass.Core };
        var a = new Analysis { Registered = new List<ModEntry> { core } };
        var parked = new ModEntry
        {
            Raw = "", Kind = EntryKind.Local, Name = "Parked", Dir = "Parked",
            Registered = false, Ignored = true,
        };
        var wanted = new ModEntry
        {
            Raw = "", Kind = EntryKind.Local, Name = "Wanted", Dir = "Wanted",
            Registered = false,
        };
        a.UnregisteredLocal.Add(parked);
        a.UnregisteredLocal.Add(wanted);

        a.BuildSuggestion();

        Assert.Contains("Wanted|edit", a.SuggestedOrder);
        Assert.DoesNotContain(a.SuggestedOrder, e => e.StartsWith("Parked"));
        Assert.DoesNotContain(a.Changes, c => c.Entry.StartsWith("Parked"));
    }
}

public class ToggleDisabledTests
{
    [Fact]
    public void RawToggledDisabled_RoundTrips()
    {
        var local = ModEntry.Parse("SomeMod|edit", Env());
        Assert.Equal("SomeMod|disabled", local.RawToggledDisabled(true));

        var disabledLocal = ModEntry.Parse("SomeMod|disabled", Env());
        Assert.Equal("SomeMod|edit", disabledLocal.RawToggledDisabled(false));   // locals get |edit back

        var ws = ModEntry.Parse(@"C:\ws\123", Env());
        Assert.Equal(@"C:\ws\123|disabled", ws.RawToggledDisabled(true));
        var wsOff = ModEntry.Parse(@"C:\ws\123|disabled", Env());
        Assert.Equal(@"C:\ws\123", wsOff.RawToggledDisabled(false));             // paths come back plain
    }

    private static GameEnv Env() => new()
    {
        GameRoot = @"C:\nonexistent-game",
        DiscoveredVia = "test",
        CoreDataDir = @"C:\nonexistent-game\data",
        ModsDir = @"C:\nonexistent-game\Mods",
    };
}

/// <summary>--json output shape, against the same synthetic install PatcherTests uses.</summary>
public class JsonReportTests : IDisposable
{
    private readonly string _root;
    private readonly GameEnv _env;

    public JsonReportTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "OstraJson_" + Guid.NewGuid().ToString("N"));
        var core = Path.Combine(_root, "core");
        var mods = Path.Combine(_root, "Mods");
        Directory.CreateDirectory(Path.Combine(core, "loot"));
        Directory.CreateDirectory(Path.Combine(mods, "ModA", "data", "loot"));
        File.WriteAllText(Path.Combine(core, "loot", "p.json"),
            """[{"strName":"PoolP","aLoots":["x=1x1"]}]""");
        File.WriteAllText(Path.Combine(mods, "ModA", "data", "loot", "p.json"),
            """[{"strName":"PoolP","aLoots":["x=1x1","a=1x1"]}]""");
        File.WriteAllText(Path.Combine(mods, "loading_order.json"),
            """[{"strName":"Mod Loading Order","aLoadOrder":["core","ModA","Dead|disabled"]}]""");
        _env = new GameEnv
        {
            GameRoot = _root, DiscoveredVia = "test",
            CoreDataDir = core, ModsDir = mods, InstalledVersion = "0.0.0.1",
        };
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Build_ProducesParseableReportWithTheExpectedShape()
    {
        var state = Engine.Analyze(_env);
        var json = JsonReport.Build(_env, state, "testver", new[] { "did a thing" });

        var root = JsonNode.Parse(json)!;
        Assert.Equal("testver", root["ostrasortVersion"]!.GetValue<string>());
        Assert.Equal("0.0.0.1", root["game"]!["version"]!.GetValue<string>());

        var mods = root["mods"]!.AsArray();
        Assert.Equal(3, mods.Count);   // core + ModA + the disabled dead entry
        Assert.Contains(mods, m => m!["name"]!.GetValue<string>() == "ModA"
                                && m["kind"]!.GetValue<string>() == "local"
                                && m["registered"]!.GetValue<bool>());
        Assert.Contains(mods, m => m!["name"]!.GetValue<string>() == "Dead"
                                && m["disabled"]!.GetValue<bool>());

        Assert.Equal("did a thing", root["performed"]!.AsArray()[0]!.GetValue<string>());
        Assert.NotNull(root["actionable"]);
        Assert.NotNull(root["suggestedOrder"]);
        Assert.NotNull(root["patch"]!["exists"]);
    }
}
