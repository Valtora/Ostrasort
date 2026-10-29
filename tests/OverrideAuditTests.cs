using System.IO;
using System.Text.Json;
using Ostrasort;
using Xunit;

namespace Ostrasort.Tests;

public class OverrideAuditTests
{
    private static Dictionary<string, string> Fields(string json) =>
        CoreHistory.HashFields(JsonDocument.Parse(json).RootElement);

    private static CoreHistory.Snapshot Snap(string version, string json) => new()
    {
        Version = version,
        Objects = { [CoreHistory.Key("items", "ItmLamp")] = Fields(json) },
    };

    private static (Analysis A, ModEntry Mod) WithMod(string modJson)
    {
        var m = new ModEntry { Raw = "Lamps", Kind = EntryKind.Local, Name = "Lamps", Dir = "Lamps" };
        m.CoreOverrideFields[("items", "ItmLamp")] = Fields(modJson);
        return (new Analysis { Registered = new() { m } }, m);
    }

    [Fact]
    public void CopyHoldingTheOldValue_UndoesTheUpdate()
    {
        var before = Snap("1.0", """{"strName":"ItmLamp","fMass":1,"fLight":5}""");
        var now = Snap("1.1", """{"strName":"ItmLamp","fMass":2,"fLight":5,"bNew":true}""");
        var (a, m) = WithMod("""{"strName":"ItmLamp","fMass":1,"fLight":9}""");

        OverrideAudit.Annotate(a, now, before);

        // fMass reverted, bNew (added by the update) dropped by the whole-object copy, fLight is the mod's own edit
        Assert.Equal(new[] { "ItmLamp: bNew, fMass" }, m.RevertedChanges);
        Assert.Contains(a.Warnings, w => w.Contains("from 1.0 to 1.1"));
        Assert.Empty(m.RedundantOverrides);
    }

    [Fact]
    public void WithFfu_OnlyFieldsTheModSetsCount()
    {
        var before = Snap("1.0", """{"strName":"ItmLamp","fMass":1}""");
        var now = Snap("1.1", """{"strName":"ItmLamp","fMass":2,"bNew":true}""");
        var (a, m) = WithMod("""{"strName":"ItmLamp","fMass":1}""");
        a.Ffu = new FfuContext { FrameworkPresent = true };

        OverrideAudit.Annotate(a, now, before);

        Assert.Equal(new[] { "ItmLamp: fMass" }, m.RevertedChanges);   // the merge keeps bNew
    }

    [Fact]
    public void CopyIdenticalToTheGame_IsRedundant_EvenWhenLaidOutDifferently()
    {
        var now = Snap("1.1", """{"strName":"ItmLamp","aTags":["a","b"]}""");
        var (a, m) = WithMod("""
            {
              "strName" : "ItmLamp",
              "aTags"   : [ "a",
                            "b" ]
            }
            """);

        OverrideAudit.Annotate(a, now, previous: null);

        Assert.Equal(new[] { "ItmLamp" }, m.RedundantOverrides);
        Assert.Empty(a.Warnings);
    }

    [Fact]
    public void DisabledMod_IsNotAudited()
    {
        var before = Snap("1.0", """{"strName":"ItmLamp","fMass":1}""");
        var now = Snap("1.1", """{"strName":"ItmLamp","fMass":2}""");
        var (a, m) = WithMod("""{"strName":"ItmLamp","fMass":1}""");
        m.Disabled = true;

        OverrideAudit.Annotate(a, now, before);

        Assert.Empty(m.RevertedChanges);
        Assert.Empty(a.Warnings);
    }

    [Fact]
    public void Record_RotatesOnlyWhenTheContentChanges()
    {
        var core = Path.Combine(Path.GetTempPath(), "OstraCore_" + Guid.NewGuid().ToString("N"));
        CoreHistory.Record(core, Snap("1.0", """{"strName":"ItmLamp","fMass":1}"""));
        Assert.Null(CoreHistory.Previous(core));

        // a verify that rewrites identical files: new fingerprint, same content
        var same = Snap("1.0", """{"strName":"ItmLamp","fMass":1}""");
        same.Fingerprint = "verified";
        CoreHistory.Record(core, same);
        Assert.Null(CoreHistory.Previous(core));
        Assert.False(CoreHistory.NeedsRecord(core, "verified"));

        CoreHistory.Record(core, Snap("1.1", """{"strName":"ItmLamp","fMass":2}"""));
        Assert.Equal("1.0", CoreHistory.Previous(core)?.Version);
        Assert.Equal("1.1", CoreHistory.Current(core)?.Version);
    }
}
