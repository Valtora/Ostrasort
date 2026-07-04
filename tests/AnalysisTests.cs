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
}
