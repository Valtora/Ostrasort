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
    public void TwoFinalSayModsOnOnePool_AreAConflictToChoose_NotResolvedByOrder()
    {
        // two character-generation mods both curate the starting-ship pool: load
        // order picks one curation and drops the other, so it must be raised -
        // and neither reordered by item count nor union-merged
        var core = Core();
        var cg1 = Mod("CharGenOne", LoadTier.Late);
        cg1.Claims[("loot", "CGEncShipbreakerShipEvents")] = new[] { "a=1x1", "b=1x1" };
        var cg2 = Mod("CharGenTwo", LoadTier.Late);
        cg2.Claims[("loot", "CGEncShipbreakerShipEvents")] = new[] { "a=1x1", "c=1x1" };
        var a = new Analysis { Registered = new() { core, cg1, cg2 } };

        a.FindCollisions();
        a.MarkOrderResolved();
        a.BuildSuggestion();

        var col = Assert.Single(a.Collisions);
        Assert.False(col.ResolvedByOrder);
        Assert.True(col.CurationClash);
        Assert.True(CollisionView.NeedsAttention(col));
        Assert.False(Patcher.IsPatchableLoot(col));
        Assert.False(a.OrderChanged);
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

    // ------------------------------------------- late tier over a plain pool ---

    /// <summary>
    /// Dez's Merchants + NewVentsandPumps. A content mod ships two CGEnc* life
    /// events for its merchant career, so it is detected late and sorts to the end
    /// of the content block. It also ships a stale copy of a kiosk inventory that
    /// drops one item the other mod stocks. That pool is nothing to do with
    /// character generation, so the late slot decides it by accident: rule 4
    /// refuses to reorder around a late mod, and rule 4.5 would undo the move
    /// anyway. Reporting it as "press Fix automatically to reorder them" sent the
    /// player after a move that never came.
    /// </summary>
    private static (Analysis A, Collision Pool) DezShape(bool manualPin = false)
    {
        var core = Core();
        var vents = Mod("NewVentsandPumps");
        vents.Claims[("loot", "ItmKioskVentandpumpShopInv")] =
            new[] { "ItmN2FilterC=1.0x1", "ItmCO2FilterC=1.0x1", "ItmFilter01Loose=1.0x1" };
        var dez = Mod("Dez's Merchants", LoadTier.Late);
        dez.CategoryManual = manualPin;
        dez.Claims[("lifeevents", "CGEncVentMerchantEvent")] = null;   // why it is detected late
        dez.Claims[("loot", "ItmKioskVentandpumpShopInv")] =
            new[] { "ItmCO2FilterC=1.0x2-3", "ItmFilter01Loose=1.0x2-10" };   // strict subset: N2 filter dropped
        var a = new Analysis { Registered = new() { core, vents, dez } };

        a.FindCollisions();
        a.MarkOrderResolved();
        return (a, a.Collisions.Single(c => c.ObjName == "ItmKioskVentandpumpShopInv"));
    }

    [Fact]
    public void OrderLocked_DetectedLateModOverPlainPool_IsPatchedNotReordered()
    {
        var (a, pool) = DezShape();

        Assert.Contains(pool.Pairs, p => p.Rel == Relation.SubsetViolation);
        Assert.True(pool.OrderLocked);
        Assert.False(pool.ResolvedByOrder);            // nothing here is decided by design - an item is lost
        Assert.True(CollisionView.NeedsAttention(pool));
        Assert.False(CollisionView.FixableByReorder(pool));   // the sort will not move it, so do not promise that
        Assert.True(Patcher.IsPatchableLoot(pool));            // the per-item union is what actually fixes it
    }

    [Fact]
    public void OrderLocked_SuggestionMakesNoMoveItCannotKeep()
    {
        // the invariant the dead end broke: anything the view calls reorder-fixable
        // must produce a real move, and anything it does not must produce none
        var (a, pool) = DezShape();

        a.BuildSuggestion();

        Assert.False(CollisionView.FixableByReorder(pool));
        Assert.DoesNotContain(a.Changes, c => c.Reason.Contains("superset"));
        Assert.Equal(new[] { "core", "NewVentsandPumps", "Dez's Merchants" }, a.SuggestedOrder);
    }

    [Fact]
    public void OrderLocked_ManualLatePin_StillWinsWholesale()
    {
        // a tier the user pinned by hand claims final say over everything the mod
        // touches, so the pool stays resolved by order and out of the patch
        var (a, pool) = DezShape(manualPin: true);

        Assert.True(pool.ResolvedByOrder);
        Assert.False(pool.OrderLocked);
        Assert.False(CollisionView.NeedsAttention(pool));
        Assert.False(Patcher.IsPatchableLoot(pool));
    }

    [Fact]
    public void OrderLocked_ViewOffersTheMergeNotTheReorder()
    {
        var (a, _) = DezShape();

        var text = string.Join("\n", CollisionView.BuildActive(a).Select(l => l.Text));

        Assert.Contains("merge them so nothing is lost", text);
        Assert.DoesNotContain("reorder them", text);
        Assert.Contains("loads late because it owns new-game / start content", text);
    }

    [Fact]
    public void SubsetViolation_BetweenOrdinaryMods_IsStillFixedByReordering()
    {
        // the other half of the invariant: with no late tier in play the sort does
        // move the superset last, so the reorder promise stays true
        var core = Core();
        var superset = Mod("Superset");
        superset.Claims[("loot", "ItmShopInv")] = new[] { "a=1x1", "b=1x1" };
        var subset = Mod("Subset");
        subset.Claims[("loot", "ItmShopInv")] = new[] { "a=1x1" };
        var a = new Analysis { Registered = new() { core, superset, subset } };

        a.FindCollisions();
        a.MarkOrderResolved();
        var pool = Assert.Single(a.Collisions);
        a.BuildSuggestion();

        Assert.False(pool.OrderLocked);
        Assert.True(CollisionView.FixableByReorder(pool));
        Assert.Contains(a.Changes, c => c.Reason.Contains("superset"));
        Assert.Equal(new[] { "core", "Subset", "Superset" }, a.SuggestedOrder);
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
