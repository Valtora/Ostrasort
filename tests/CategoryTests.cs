using System.IO;
using Ostrasort;
using Xunit;
using static Ostrasort.Tests.TestData;

namespace Ostrasort.Tests;

public class CategoryTests
{
    private static ModEntry Core() =>
        new() { Raw = "core", Kind = EntryKind.Core, Name = "core", Dir = "core", Class = ModClass.Core };

    private static ModEntry Mod(string name, LoadTier tier = LoadTier.Normal, ModClass cls = ModClass.DataOverride)
        => new() { Raw = name, Kind = EntryKind.Local, Name = name, Dir = name, Class = cls, Tier = tier };

    private static GameEnv FakeEnv() => new()
    {
        GameRoot = @"C:\nonexistent-game",
        DiscoveredVia = "test",
        CoreDataDir = @"C:\nonexistent-game\Ostranauts_Data\StreamingAssets\data",
        ModsDir = @"C:\nonexistent-game\Ostranauts_Data\Mods",
    };

    // ------------------------------------------------------------- detection ---

    [Fact]
    public void Detect_LifeeventsAndCgObjects_IsCharacterGeneration()
    {
        // mirrors Vanilla Plus Character Generation: life-events + the CGEnc namespace
        var m = TestData.Mod("VPCC");
        m.Claims[("lifeevents", "CGEncShipbreakerPrizeShipIntro")] = null;
        m.Claims[("interactions", "CGEncCrackdownIntro")] = null;
        m.Claims[("loot", "CGEncShipbreakerEvent")] = System.Array.Empty<string>();

        var (cat, _) = CategoryAnalysis.Detect(m);

        Assert.Equal(ModCategory.CharacterGeneration, cat);
        Assert.Equal(LoadTier.Late, CategoryAnalysis.DefaultTier(cat));
    }

    [Fact]
    public void Detect_AuthorNamespacedCgClone_IsCharacterGeneration()
    {
        // a new object named in the author's namespace but still carrying CGEnc
        var m = TestData.Mod("CGMod");
        m.Claims[("interactions", "HLVpCGEncShipbreakerShipEventsRoot")] = null;
        Assert.Equal(ModCategory.CharacterGeneration, CategoryAnalysis.Detect(m).Category);
    }

    [Fact]
    public void Detect_ShipContent_IsShipsAndStations_Normal()
    {
        var m = TestData.Mod("StarterShip");
        m.Claims[("ships", "MyStarter")] = null;
        m.Claims[("shipspecs", "MyStarterSpec")] = null;

        var (cat, _) = CategoryAnalysis.Detect(m);

        Assert.Equal(ModCategory.ShipsAndStations, cat);
        Assert.Equal(LoadTier.Normal, CategoryAnalysis.DefaultTier(cat));
    }

    // --------------------------------------------------------------- sorting ---

    [Fact]
    public void Suggestion_MovesCharacterGenModLast()
    {
        // a character-generation mod sitting mid-list is bubbled to the end of the
        // content block so its choices win over later content
        var core = Core();
        var cg = Mod("VPCC", LoadTier.Late);
        var ship = Mod("StarterShip");
        var a = new Analysis { Registered = new() { core, cg, ship } };   // cg BEFORE ship

        a.BuildSuggestion();

        Assert.Equal(new[] { "core", "StarterShip", "VPCC" }, a.SuggestedOrder);
        Assert.True(a.OrderChanged);
        Assert.Contains(a.Changes, c => c.Entry == "VPCC" && c.Reason.Contains("loads late"));
    }

    [Fact]
    public void Suggestion_DoesNotPushFinalSayModUp_OnSubsetLoot()
    {
        // THE VPCC REGRESSION: a ship mod stocks the superset of a character-gen
        // pool the CG mod curates down to a subset. The pure quantity heuristic
        // would move the superset below the CG mod (pushing the CG mod up); the
        // final-say guard must keep the CG mod loading last.
        var core = Core();
        var ship = Mod("StarterShip");
        ship.Claims[("loot", "CGEncShipbreakerShipEvents")] = new[] { "a=1x1", "b=1x1", "c=1x1", "d=1x1" };
        var cg = Mod("VPCC", LoadTier.Late);
        cg.Claims[("loot", "CGEncShipbreakerShipEvents")] = new[] { "a=1x1" };
        var a = new Analysis { Registered = new() { core, ship, cg } };   // cg already last

        a.FindCollisions();
        a.BuildSuggestion();

        Assert.Equal(new[] { "core", "StarterShip", "VPCC" }, a.SuggestedOrder);
        Assert.DoesNotContain(a.Changes, c => c.Reason.Contains("superset"));
    }

    [Fact]
    public void Suggestion_ManualEarlyPin_MovesModToFrontOfContent()
    {
        var core = Core();
        var a1 = Mod("First");
        var pinned = Mod("Yielder", LoadTier.Early);
        pinned.CategoryManual = true;
        var a = new Analysis { Registered = new() { core, a1, pinned } };

        a.BuildSuggestion();

        Assert.Equal(new[] { "core", "Yielder", "First" }, a.SuggestedOrder);
        Assert.Contains(a.Changes, c => c.Entry == "Yielder" && c.Reason.Contains("early"));
    }

    // ----------------------------------------------------------- aCOs-aware ---

    [Fact]
    public void FindCollisions_ComparesACOs_NotJustALoots()
    {
        // both pools have an EMPTY aLoots and route everything through aCOs (the
        // character-generation pattern) - the relation must come from aCOs, or
        // Ostrasort would read the two overrides as identical-empty
        var earlier = TestData.Mod("Ship");
        earlier.Claims[("loot", "CGEncShipbreakerShipEvents")] = System.Array.Empty<string>();
        earlier.CoClaims[("loot", "CGEncShipbreakerShipEvents")] = new[] { "a=1x1", "b=1x1", "c=1x1" };
        var later = TestData.Mod("VPCC");
        later.Claims[("loot", "CGEncShipbreakerShipEvents")] = System.Array.Empty<string>();
        later.CoClaims[("loot", "CGEncShipbreakerShipEvents")] = new[] { "a=1x1" };
        var a = new Analysis { Registered = new() { Core(), earlier, later } };

        a.FindCollisions();

        var col = Assert.Single(a.Collisions);
        var pair = Assert.Single(col.Pairs);
        Assert.Equal(Relation.SubsetViolation, pair.Rel);
        Assert.Equal(2, pair.LostFromEarlier.Length);   // b, c dropped
    }

    // ------------------------------------------------ final-say / patch ---

    [Fact]
    public void MarkOrderResolved_FinalSayModWins_ExcludedFromPatch()
    {
        // CG mod curates a pool it loads last: partial overlap would normally be
        // patchable, but a per-item union would re-add what it removed, so it is
        // resolved by load order and NOT patched
        var core = Core();
        var ship = Mod("StarterShip");
        ship.Claims[("loot", "CGEncShipbreakerEvent")] = new[] { "a=1x1", "b=1x1" };
        var cg = Mod("VPCC", LoadTier.Late);
        cg.Claims[("loot", "CGEncShipbreakerEvent")] = new[] { "a=1x1", "z=1x1" };   // partial overlap
        var a = new Analysis { Registered = new() { core, ship, cg } };

        a.FindCollisions();
        var col = Assert.Single(a.Collisions);
        Assert.Contains(col.Pairs, p => p.Rel == Relation.Partial);   // would be patchable...
        a.MarkOrderResolved();

        Assert.True(col.ResolvedByOrder);
        Assert.False(CollisionView.NeedsAttention(col));
        Assert.Empty(Patcher.PatchableConflicts(a));   // ...but the final-say mod wins by order instead
    }

    [Fact]
    public void MarkOrderResolved_FinalSayModNotLast_StillNeedsAttention()
    {
        // if the CG mod is NOT last, another mod overrides it - that IS a problem
        // the suggestion should fix, so it must still read as needing attention
        var core = Core();
        var cg = Mod("VPCC", LoadTier.Late);
        cg.Claims[("loot", "CGEncShipbreakerEvent")] = new[] { "a=1x1" };
        var ship = Mod("StarterShip");
        ship.Claims[("loot", "CGEncShipbreakerEvent")] = new[] { "a=1x1", "b=1x1" };
        var a = new Analysis { Registered = new() { core, cg, ship } };   // ship loads AFTER cg

        a.FindCollisions();
        var col = Assert.Single(a.Collisions);
        a.MarkOrderResolved();

        Assert.False(col.ResolvedByOrder);   // ship (not the CG mod) is the last claimant
    }

    // -------------------------------------------------- manual override store ---

    [Fact]
    public void CategoryOverrideList_PersistsAndReloads()
    {
        var path = Path.Combine(Path.GetTempPath(), "OstraCat_" + System.Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var list = new CategoryOverrideList(path);
            list.Set("k1", LoadTier.Late);
            list.Set("k2", LoadTier.Early);

            var reloaded = new CategoryOverrideList(path);
            Assert.True(reloaded.TryGet("k1", out var t1) && t1 == LoadTier.Late);
            Assert.True(reloaded.TryGet("k2", out var t2) && t2 == LoadTier.Early);

            reloaded.Clear("k1");
            Assert.False(new CategoryOverrideList(path).TryGet("k1", out _));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Classify_ManualPin_OverridesDetection()
    {
        var path = Path.Combine(Path.GetTempPath(), "OstraCat_" + System.Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var env = FakeEnv();
            var ship = TestData.Mod("StarterShip");
            ship.Claims[("ships", "X")] = null;   // would auto-detect ShipsAndStations / Normal
            var a = new Analysis { Registered = new() { Core(), ship } };

            var overrides = new CategoryOverrideList(path);
            overrides.Set(CategoryOverrideList.KeyFor(env, ship), LoadTier.Late);
            CategoryAnalysis.Classify(env, a, overrides);

            Assert.Equal(LoadTier.Late, ship.Tier);
            Assert.True(ship.CategoryManual);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
