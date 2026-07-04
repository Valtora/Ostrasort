using System.Text.Json.Nodes;
using Ostrasort;
using Xunit;
using static Ostrasort.Tests.TestData;

namespace Ostrasort.Tests;

public class ObjectMergeTests
{
    private static readonly ModEntry A = Mod("ModA");
    private static readonly ModEntry B = Mod("ModB");

    [Fact]
    public void DisjointFields_AutoMerge_KeepsBothChanges()
    {
        var core = Obj("""{"strName":"X","a":1,"b":1}""");
        var plan = ObjectMerge.Build(Coll("condowners", "X", A, B), core,
            Versions((A, """{"strName":"X","a":2,"b":1}"""),    // A changed a
                     (B, """{"strName":"X","a":1,"b":2}""")));   // B changed b

        Assert.NotNull(plan);
        Assert.All(plan!.Fields, f => Assert.False(f.NeedsDecision));   // nothing contested
        var merged = ObjectMerge.Assemble(plan);
        Assert.Equal(2, merged["a"]!.GetValue<int>());   // A's change
        Assert.Equal(2, merged["b"]!.GetValue<int>());   // B's change - both kept
    }

    [Fact]
    public void SameFieldDifferentValue_IsContested()
    {
        var core = Obj("""{"strName":"X","a":1}""");
        var plan = ObjectMerge.Build(Coll("condowners", "X", A, B), core,
            Versions((A, """{"strName":"X","a":2}"""),
                     (B, """{"strName":"X","a":3}""")));

        var field = Assert.Single(plan!.Fields);
        Assert.True(field.Contested);
        Assert.True(field.NeedsDecision);

        field.ChosenSourceId = field.Options.First(o => o.SourceLabel == "ModA").SourceId;
        Assert.Equal(2, ObjectMerge.Assemble(plan)["a"]!.GetValue<int>());
    }

    [Fact]
    public void ConflictingArray_OffersUnion()
    {
        var core = Obj("""{"strName":"X","aTags":["x"]}""");
        var plan = ObjectMerge.Build(Coll("condowners", "X", A, B), core,
            Versions((A, """{"strName":"X","aTags":["x","y"]}"""),
                     (B, """{"strName":"X","aTags":["x","z"]}""")));

        var field = Assert.Single(plan!.Fields);
        Assert.True(field.IsArrayField);
        var union = field.Options.FirstOrDefault(o => o.SourceId == "__union__");
        Assert.NotNull(union);

        field.ChosenSourceId = "__union__";
        var merged = (JsonArray)ObjectMerge.Assemble(plan)["aTags"]!;
        Assert.Equal(new[] { "x", "y", "z" }, merged.Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public void FieldRemovedByOneMod_AutoRemoves()
    {
        var core = Obj("""{"strName":"X","a":1,"b":1}""");
        var plan = ObjectMerge.Build(Coll("condowners", "X", A, B), core,
            Versions((A, """{"strName":"X","a":1}"""),          // A removed b
                     (B, """{"strName":"X","a":1,"b":1}""")));   // B unchanged

        Assert.NotNull(plan);
        var merged = ObjectMerge.Assemble(plan!);
        Assert.Null(merged["b"]);   // removal honoured
    }

    [Fact]
    public void IdenticalOverrides_NothingToMerge()
    {
        var core = Obj("""{"strName":"X","a":1}""");
        var plan = ObjectMerge.Build(Coll("condowners", "X", A, B), core,
            Versions((A, """{"strName":"X","a":2}"""),
                     (B, """{"strName":"X","a":2}""")));   // both make the SAME change

        // both agree on a=2: a single distinct value, auto-resolved, not contested
        var field = Assert.Single(plan!.Fields);
        Assert.False(field.Contested);
        Assert.Equal(2, ObjectMerge.Assemble(plan)["a"]!.GetValue<int>());
    }

    [Fact]
    public void ExcludedField_RevertsToVanilla()
    {
        var core = Obj("""{"strName":"X","a":1}""");
        var plan = ObjectMerge.Build(Coll("condowners", "X", A, B), core,
            Versions((A, """{"strName":"X","a":2}"""),
                     (B, """{"strName":"X","a":3}""")));

        var field = Assert.Single(plan!.Fields);
        field.Excluded = true;                       // keep vanilla
        Assert.Equal(1, ObjectMerge.Assemble(plan)["a"]!.GetValue<int>());
    }
}
