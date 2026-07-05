using System.IO;
using Ostrasort;
using Xunit;
using static Ostrasort.Tests.TestData;

namespace Ostrasort.Tests;

public class AnalysisTests
{
    private static ModEntry Registered(string raw, EntryKind kind, string name, ModClass cls = ModClass.DataAdditive)
        => new() { Raw = raw, Kind = kind, Name = name, Dir = name, Class = cls };

    private static Analysis WithOrder(params ModEntry[] mods) => new() { Registered = mods.ToList() };

    [Fact]
    public void Suggestion_PinsInfrastructureRightAfterCore()
    {
        var core = new ModEntry { Raw = "core", Kind = EntryKind.Core, Name = "core", Dir = "core", Class = ModClass.Core };
        var data = Registered("Data", EntryKind.Local, "Data", ModClass.DataOverride);
        var infra = Registered("BepInEx", EntryKind.Workshop, "BepInEx", ModClass.Infrastructure);
        var a = WithOrder(core, data, infra);   // infra is last - should move to #2

        a.BuildSuggestion();

        Assert.Equal(new[] { "core", "BepInEx", "Data" }, a.SuggestedOrder);
        Assert.True(a.OrderChanged);
    }

    [Fact]
    public void Suggestion_RemovesDeadEntry()
    {
        var core = new ModEntry { Raw = "core", Kind = EntryKind.Core, Name = "core", Dir = "core", Class = ModClass.Core };
        var live = Registered("Live", EntryKind.Local, "Live");
        var dead = new ModEntry { Raw = "Dead", Kind = EntryKind.Local, Name = "Dead", Dir = null };   // no dir = dead
        var a = WithOrder(core, live, dead);

        a.BuildSuggestion();

        Assert.DoesNotContain("Dead", a.SuggestedOrder);
        Assert.Contains(a.Changes, c => c.Action == "remove");
    }

    [Fact]
    public void Suggestion_DropsDuplicateEntry()
    {
        var core = new ModEntry { Raw = "core", Kind = EntryKind.Core, Name = "core", Dir = "core", Class = ModClass.Core };
        var a1 = Registered("Dup", EntryKind.Local, "Dup");
        var a2 = Registered("Dup", EntryKind.Local, "Dup");   // same identity twice
        var a = WithOrder(core, a1, a2);

        a.BuildSuggestion();

        Assert.Single(a.SuggestedOrder, "Dup");
    }

    [Fact]
    public void ValidateOrder_BlocksWhenCoreNotFirst()
    {
        var core = new ModEntry { Raw = "core", Kind = EntryKind.Core, Name = "core", Dir = "core", Class = ModClass.Core };
        var data = Registered("Data", EntryKind.Local, "Data");
        var issues = Analysis.ValidateOrder(new List<ModEntry> { data, core });   // core not first

        Assert.Contains(issues, i => i.StartsWith("BLOCK:"));
    }

    [Fact]
    public void CleanOrder_NoChanges()
    {
        var core = new ModEntry { Raw = "core", Kind = EntryKind.Core, Name = "core", Dir = "core", Class = ModClass.Core };
        var infra = Registered("BepInEx", EntryKind.Workshop, "BepInEx", ModClass.Infrastructure);
        var data = Registered("Data", EntryKind.Local, "Data", ModClass.DataOverride);
        var a = WithOrder(core, infra, data);

        a.BuildSuggestion();

        Assert.False(a.OrderChanged);
    }

    // ------------------------------------------------------------ collisions ---

    private static ModEntry WithLoot(string name, params string[] loots)
    {
        var m = Registered(name, EntryKind.Local, name);
        m.Claims[("loot", "Pool")] = loots;
        return m;
    }

    [Fact]
    public void FindCollisions_DetectsNonAdjacentPartialOverlap()
    {
        // adjacent pairs look clean (superset, then subset) but A vs C is a
        // partial overlap only an all-pairs comparison catches
        var a1 = WithLoot("A", "a=1x1", "b=1x1");
        var b = WithLoot("B", "a=1x1", "b=1x1", "c=1x1", "d=1x1");
        var c = WithLoot("C", "c=1x1", "d=1x1");
        var core = new ModEntry { Raw = "core", Kind = EntryKind.Core, Name = "core", Dir = "core", Class = ModClass.Core };
        var a = WithOrder(core, a1, b, c);

        a.FindCollisions();

        var col = Assert.Single(a.Collisions);
        Assert.Contains(col.Pairs, p => p.Rel == Relation.Partial && p.Earlier == a1 && p.Later == c);
        Assert.True(a.HasUnresolvedConflicts);
    }

    [Fact]
    public void FindCollisions_SkipsDisabledMods()
    {
        var live = WithLoot("Live", "a=1x1");
        var off = new ModEntry
        {
            Raw = "Off|disabled", Kind = EntryKind.Local, Name = "Off", Dir = "Off", Disabled = true,
        };
        off.Claims[("loot", "Pool")] = new[] { "b=1x1" };
        var core = new ModEntry { Raw = "core", Kind = EntryKind.Core, Name = "core", Dir = "core", Class = ModClass.Core };
        var a = WithOrder(core, live, off);

        a.FindCollisions();

        Assert.Empty(a.Collisions);   // a disabled entry never loads, so it cannot collide
    }

    // -------------------------------------------------------- entry parsing ---

    private static GameEnv FakeEnv() => new()
    {
        GameRoot = @"C:\nonexistent-game",
        DiscoveredVia = "test",
        CoreDataDir = @"C:\nonexistent-game\Ostranauts_Data\StreamingAssets\data",
        ModsDir = @"C:\nonexistent-game\Ostranauts_Data\Mods",
    };

    [Theory]
    [InlineData("SomeMod|disabled", "SomeMod", true, false)]
    [InlineData("SomeMod|edit", "SomeMod", false, true)]
    [InlineData("SomeMod", "SomeMod", false, false)]
    [InlineData("Some|weird|entry", "Some", true, false)]   // 3+ parts = disabled, like the game
    public void Parse_LocalMarkers(string raw, string name, bool disabled, bool edit)
    {
        var m = ModEntry.Parse(raw, FakeEnv());
        Assert.Equal(EntryKind.Local, m.Kind);
        Assert.Equal(name, m.Name);
        Assert.Equal(disabled, m.Disabled);
        Assert.Equal(edit, m.EditMarker);
    }

    [Fact]
    public void Parse_DisabledWorkshopPath_ResolvesTheRealFolder()
    {
        // a disabled Workshop entry must not read as a dead path (that would
        // suggest removal - and the game would re-add it ENABLED)
        var dir = Path.Combine(Path.GetTempPath(), "OstraWs_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "1234567"));
        try
        {
            var m = ModEntry.Parse(Path.Combine(dir, "1234567") + "|disabled", FakeEnv());
            Assert.Equal(EntryKind.Workshop, m.Kind);
            Assert.Equal("1234567", m.Name);
            Assert.True(m.Disabled);
            Assert.NotNull(m.Dir);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ---------------------------------------------------- game-version note ---

    [Fact]
    public void GameVersionNote_WordsBothDirections()
    {
        var m = TestData.Mod("M");
        m.GameVersion = "0.14.0.1";
        Assert.Contains("predates", m.GameVersionNote("0.15.1.6"));

        m.GameVersion = "0.16.0.0";
        Assert.Contains("newer", m.GameVersionNote("0.15.1.6"));

        m.GameVersion = "0.15.1.6";
        Assert.Null(m.GameVersionNote("0.15.1.6"));
        Assert.Null(m.GameVersionNote(null));
    }
}
