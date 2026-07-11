using System.IO;
using Ostrasort;
using Xunit;

namespace Ostrasort.Tests;

public class LogCorrelationTests
{
    private static ModEntry Mod(string name, string[]? dataFiles = null, (string Type, string Name)[]? claims = null)
    {
        var m = new ModEntry { Raw = name, Kind = EntryKind.Local, Name = name, Dir = name, DisplayName = name };
        foreach (var f in dataFiles ?? Array.Empty<string>()) m.DataFiles.Add(f);
        foreach (var c in claims ?? Array.Empty<(string, string)>()) m.Claims[c] = null;
        return m;
    }

    private static Analysis With(params ModEntry[] mods) => new() { Registered = mods.ToList() };

    private static List<LogCorrelation.LogIssue> Collect(Analysis a, params string[] lines) =>
        LogCorrelation.Collect(a, lines);

    [Fact]
    public void CodeMod_AttributedByBepInExSourceTag()
    {
        var a = With(Mod("Ship's Water"));
        var issue = Assert.Single(Collect(a, "[Error  :Ship's Water] Harmony patch target not found"));
        Assert.Equal("Error", issue.Severity);
        Assert.Equal("Ship's Water", issue.Mod?.DisplayName);
    }

    [Fact]
    public void FrameworkBepInExLines_AreIgnored()
    {
        var a = With(Mod("Ship's Water"));
        Assert.Empty(Collect(a, "[Warning:   BepInEx] Skipping duplicate assembly"));
    }

    [Fact]
    public void DataError_AttributedByUniqueJsonFilename()
    {
        var a = With(Mod("Evil Mod", dataFiles: new[] { "condowners/evil.json" }),
                     Mod("Other", dataFiles: new[] { "items/other.json" }));
        var issue = Assert.Single(Collect(a, "#Error# Failed to parse condowners/evil.json at line 3"));
        Assert.Equal("Evil Mod", issue.Mod?.DisplayName);
    }

    [Fact]
    public void DataError_AmbiguousFilename_IsUnattributedWithCandidates()
    {
        var a = With(Mod("ModA", dataFiles: new[] { "loot/loot.json" }),
                     Mod("ModB", dataFiles: new[] { "loot/loot.json" }));
        var issue = Assert.Single(Collect(a, "#Warning# bad entry in loot/loot.json"));
        Assert.Null(issue.Mod);                                  // not guessed
        Assert.Equal(2, issue.Candidates.Count);                 // both listed as candidates
    }

    [Fact]
    public void DataError_AttributedByLoadingContext_WhenLineHasNoFilename()
    {
        var a = With(Mod("Evil Mod", dataFiles: new[] { "condowners/evil.json" }));
        // the game names the file on one line, then errors on the next
        var issue = Assert.Single(Collect(a,
            "#Info# Loading json: condowners/evil.json from byte array",
            "#Error# NullReferenceException while building the object"));
        Assert.Equal("Evil Mod", issue.Mod?.DisplayName);
    }

    [Fact]
    public void DataError_FallsBackToClaimedStrName()
    {
        var a = With(Mod("Sink Mod", claims: new[] { ("condowners", "AABarTechnoLowPass") }));
        var issue = Assert.Single(Collect(a, "#Error# condition AABarTechnoLowPass references a missing trigger"));
        Assert.Equal("Sink Mod", issue.Mod?.DisplayName);
    }

    [Fact]
    public void NonIssueLines_Ignored_AndIdenticalLinesDeduped()
    {
        var a = With(Mod("ModA", dataFiles: new[] { "loot/a.json" }));
        var issues = Collect(a,
            "#Info# Loading json: ships/B-0Y0.json from byte array",       // benign
            "Possible recursion found in CT: TIsFoo",                       // benign
            "#Error# broke loot/a.json",
            "#Error# broke loot/a.json");                                   // identical -> deduped
        Assert.Single(issues);
    }

    [Fact]
    public void Annotate_AddsAttributedWarningAndPerModNote()
    {
        var mod = Mod("Evil Mod", dataFiles: new[] { "condowners/evil.json" });
        var a = With(mod);
        var log = Path.Combine(Path.GetTempPath(), "OstraLog_" + Guid.NewGuid().ToString("N") + ".log");
        File.WriteAllText(log, "#Error# Failed to parse condowners/evil.json\n");
        try
        {
            LogCorrelation.Annotate(a, log);
            Assert.Contains(a.Warnings, w => w.Contains("Evil Mod") && w.Contains("last game launch"));
            Assert.NotEmpty(mod.LogNotes);
        }
        finally { File.Delete(log); }
    }
}
