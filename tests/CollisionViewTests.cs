using Ostrasort;
using Xunit;
using static Ostrasort.Tests.TestData;

namespace Ostrasort.Tests;

public class CollisionViewTests
{
    private static Analysis WithCollisions(params Collision[] cols)
    {
        var a = new Analysis { Registered = new() };
        foreach (var c in cols) a.Collisions.Add(c);
        return a;
    }

    private static string Active(Analysis a) => string.Join("\n", CollisionView.BuildActive(a).Select(v => v.Text));
    private static string Handled(Analysis a) => string.Join("\n", CollisionView.BuildResolved(a).Select(v => v.Text));

    [Fact]
    public void PatchResolvedCollisions_LeaveTheCollisionsTab_AndAppearUnderHandled()
    {
        var m1 = Mod("Sundries"); var m2 = Mod("StasisBed");
        var active = Coll("loot", "ActivePool", m1, m2);
        active.Pairs.Add(new PairRelation(m1, m2, Relation.Partial, ["ItmX"], ["ItmY"]));   // a real conflict
        var resolved = Coll("loot", "ResolvedPool", m1, m2);
        resolved.ResolvedByPatch = true;

        var a = WithCollisions(active, resolved);

        Assert.Contains("ActivePool", Active(a));
        Assert.DoesNotContain("ResolvedPool", Active(a));
        Assert.Contains("ResolvedPool", Handled(a));
        Assert.DoesNotContain("ActivePool", Handled(a));
    }

    [Fact]
    public void AllHandled_CollisionsTabShowsCleanState_WithHint()
    {
        var only = Coll("loot", "OnlyPool", Mod("A"), Mod("B"));
        only.ResolvedByPatch = true;

        var activeText = Active(WithCollisions(only));

        Assert.DoesNotContain("OnlyPool", activeText);           // nothing needs action -> tab reads clean
        Assert.Contains("Handled automatically", activeText);    // the empty-state hint points to the other tab
    }

    [Fact]
    public void SameSetLoot_LeavesTheCollisionsTabClean_AndAppearsUnderHandled()
    {
        // "same items, different quantities" - the load order handles it, nothing lost
        var m1 = Mod("Ithalan"); var m2 = Mod("HeavyTug");
        var benign = Coll("loot", "RandomShipBrokerOKLG", m1, m2);
        benign.Pairs.Add(new PairRelation(m1, m2, Relation.Equal, [], []));

        Assert.False(CollisionView.NeedsAttention(benign));

        var a = WithCollisions(benign);
        Assert.DoesNotContain("RandomShipBrokerOKLG", Active(a));    // Collisions tab reads clean
        Assert.Contains("No conflicts need action", Active(a));
        Assert.Contains("RandomShipBrokerOKLG", Handled(a));         // still visible under Resolved / handled
        Assert.Contains("same items, different quantities", Handled(a));
    }

    [Fact]
    public void PartialLootConflict_StaysOnTheCollisionsTab()
    {
        var m1 = Mod("A"); var m2 = Mod("B");
        var real = Coll("loot", "Pool", m1, m2);
        real.Pairs.Add(new PairRelation(m1, m2, Relation.Partial, ["ItmX"], ["ItmY"]));

        Assert.True(CollisionView.NeedsAttention(real));

        var a = WithCollisions(real);
        Assert.Contains("Pool", Active(a));
        Assert.DoesNotContain("Pool", Handled(a));
    }

    [Fact]
    public void MixedGroup_RealConflictOnCollisions_BenignUnderHandled()
    {
        var m1 = Mod("A"); var m2 = Mod("B");
        var real = Coll("loot", "RealPool", m1, m2);
        real.Pairs.Add(new PairRelation(m1, m2, Relation.Partial, ["ItmX"], ["ItmY"]));
        var benign = Coll("loot", "BenignPool", m1, m2);
        benign.Pairs.Add(new PairRelation(m1, m2, Relation.Equal, [], []));

        var a = WithCollisions(real, benign);
        Assert.Equal(1, a.Collisions.Count(CollisionView.NeedsAttention));

        Assert.Contains("RealPool", Active(a));
        Assert.DoesNotContain("BenignPool", Active(a));
        Assert.Contains("BenignPool", Handled(a));
        Assert.DoesNotContain("RealPool", Handled(a));
    }
}
