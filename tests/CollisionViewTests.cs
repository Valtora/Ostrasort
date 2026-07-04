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

    [Fact]
    public void PatchResolvedCollisions_LeaveTheActiveTab_AndAppearInResolved()
    {
        var m1 = Mod("Sundries"); var m2 = Mod("StasisBed");
        var active = Coll("loot", "ActivePool", m1, m2);
        var resolved = Coll("loot", "ResolvedPool", m1, m2);
        resolved.ResolvedByPatch = true;

        var a = WithCollisions(active, resolved);
        var activeText = string.Join("\n", CollisionView.BuildActive(a).Select(v => v.Text));
        var resolvedText = string.Join("\n", CollisionView.BuildResolved(a).Select(v => v.Text));

        Assert.Contains("ActivePool", activeText);
        Assert.DoesNotContain("ResolvedPool", activeText);
        Assert.Contains("ResolvedPool", resolvedText);
        Assert.DoesNotContain("ActivePool", resolvedText);
    }

    [Fact]
    public void AllResolved_ActiveTabShowsCleanState_WithHint()
    {
        var only = Coll("loot", "OnlyPool", Mod("A"), Mod("B"));
        only.ResolvedByPatch = true;

        var activeText = string.Join("\n", CollisionView.BuildActive(WithCollisions(only)).Select(v => v.Text));

        Assert.DoesNotContain("OnlyPool", activeText);
        Assert.Contains("resolved by the patch", activeText);   // the tailored empty-state hint
    }
}
