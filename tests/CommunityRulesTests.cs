using System.IO;
using Ostrasort;
using Xunit;

namespace Ostrasort.Tests;

public class CommunityRulesTests
{
    private static readonly GameEnv Env = new()
    {
        GameRoot = @"C:\g", DiscoveredVia = "test", CoreDataDir = @"C:\g\core", ModsDir = @"C:\g\Mods",
        InstalledVersion = "1.0.1.8",
    };

    private static ModEntry Ws(string id, string name) =>
        new() { Raw = $@"C:\ws\{id}", Kind = EntryKind.Workshop, Name = id, Dir = $@"C:\ws\{id}", DisplayName = name };

    private static CommunityRules Rules(string mods) =>
        CommunityRules.Parse("""{"version":1,"updated":"2026-10-29","mods":{""" + mods + "}}")!;

    [Fact]
    public void BundledCopy_ParsesWithItsLeadingComments()
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "rules", "community-rules.json"));
        Assert.NotNull(CommunityRules.Parse(text));
    }

    [Fact]
    public void LoadAfter_MovesTheModBehindItsDependency()
    {
        var a = new Analysis { Registered = new() { new ModEntry { Raw = "core", Kind = EntryKind.Core, Name = "core" }, Ws("2", "Addon"), Ws("1", "Base") } };
        Rules("""
            "2": { "loadAfter": ["1"] }
            """).Apply(Env, a);

        a.BuildSuggestion(tidy: false);

        Assert.Equal(new[] { "core", @"C:\ws\1", @"C:\ws\2" }, a.SuggestedOrder);
        Assert.Contains(a.Changes, c => c.Reason.Contains("community rules"));
    }

    [Fact]
    public void Tier_NeverReplacesAPlayerPin()
    {
        var pinned = Ws("1", "Pinned");
        pinned.CategoryManual = true;
        pinned.Tier = LoadTier.Early;
        var free = Ws("2", "Free");
        var a = new Analysis { Registered = new() { pinned, free } };

        Rules("""
            "1": { "tier": "late" }, "2": { "tier": "late" }
            """).Apply(Env, a);

        Assert.Equal(LoadTier.Early, pinned.Tier);
        Assert.Equal(LoadTier.Late, free.Tier);
    }

    [Fact]
    public void KnownProblems_WarnOnlyWhenTheyApply()
    {
        var a = new Analysis { Registered = new() { Ws("1", "Old"), Ws("2", "Rival"), Ws("3", "Fine") } };
        Rules("""
            "1": { "brokenOn": ["1.0.1.*"], "brokenNote": "crashes on load", "replacedBy": { "id": "9", "name": "New" } },
            "2": { "incompatibleWith": [ { "id": "1", "reason": "both patch the same method" } ] },
            "3": { "brokenOn": ["0.15.*"], "incompatibleWith": [ { "id": "404" } ] }
            """).Apply(Env, a);

        Assert.Contains(a.Warnings, w => w.Contains("'Old' is known to be broken on game 1.0.1.8 (crashes on load)"));
        Assert.Contains(a.Warnings, w => w.Contains("replaced by New"));
        Assert.Single(a.Warnings, w => w.Contains("not to work together"));
        Assert.DoesNotContain(a.Warnings, w => w.Contains("'Fine'"));
    }

    [Fact]
    public void FfuTag_PutsAnUnmarkedModInTheFfuBlock()
    {
        var m = Ws("1", "Quiet FFU Mod");
        var a = new Analysis { Registered = new() { m } };

        FfuAnalysis.Classify(Env, a, rules: Rules("""
            "1": { "ffu": true }
            """));

        Assert.True(m.IsFfu);
        Assert.Contains(m.FfuSignals, s => s.Contains("community rules"));
    }

    [Fact]
    public void LocalCopyOfAWorkshopMod_MatchesThroughItsPublishedId()
    {
        var local = new ModEntry { Raw = "Mine|edit", Kind = EntryKind.Local, Name = "Mine", Dir = "Mine", PublishedId = "77" };
        Assert.NotNull(Rules("""
            "77": { "note": "x" }
            """).For(local));
    }

    [Theory]
    [InlineData("1.0.1.8", true)]
    [InlineData("1.0.1.*", true)]
    [InlineData("1.0.*", true)]
    [InlineData("*", true)]
    [InlineData("1.0.1.9", false)]
    [InlineData("1.0.10.*", false)]
    public void VersionMatches(string pattern, bool expected) =>
        Assert.Equal(expected, CommunityRules.VersionMatches(pattern, "1.0.1.8"));
}
