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
    public void MissingMethod_ForAFlaggedPlugin_IsPinnedOnItsMod()
    {
        var a = With(Mod("ShipsWater"), Mod("Other", claims: new[] { ("condowner", "DataHandler") }));
        a.CodeModIssues.Add(new CodeModIssue(@"C:\g\BepInEx\plugins\ShipsWater\ShipsWater.dll", "ShipsWater",
            new[] { "method DataHandler.GetCondTrigger" }));

        var issue = Assert.Single(Collect(a,
            "MissingMethodException: Method not found: int DataHandler.GetCondTrigger(string)"));
        Assert.Equal("ShipsWater", issue.Mod?.Name);
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
    public void RuntimeException_FarFromLoadContext_IsNotPinnedOnTheLastLoadedFile()
    {
        // the "Loading json:" context must expire once ordinary lines resume -
        // a gameplay exception hours later is not the last-loaded mod's fault
        var a = With(Mod("Ship Mod", dataFiles: new[] { "ships/x.json" }));
        var issues = Collect(a,
            "#Info# Loading json: ships/x.json from byte array",
            "Unloading 5 unused Assets",                                   // normal log flow resumes
            "NullReferenceException: Object reference not set");           // unrelated, much later
        var issue = Assert.Single(issues);
        Assert.Null(issue.Mod);
        Assert.Empty(issue.Candidates);
    }

    [Fact]
    public void LoadingContext_DoesNotLeakAcrossLogFiles()
    {
        var a = With(Mod("Ship Mod", dataFiles: new[] { "ships/x.json" }));
        var issues = LogCorrelation.Collect(a, new IReadOnlyList<string>[]
        {
            new[] { "#Info# Loading json: ships/x.json from byte array" },   // Player.log ends mid-load
            new[] { "Some stray Exception in the BepInEx log" },             // different file entirely
        });
        var issue = Assert.Single(issues);
        Assert.Null(issue.Mod);
    }

    [Fact]
    public void PatchOwnFileError_IsNotBlamedOnAnInnocentSourceMod()
    {
        // the generated patch ships loot/loot.json; so does exactly one source
        // mod. The error must not be uniquely pinned on the source mod - the
        // patch is a candidate too.
        var patchDir = Path.Combine(Path.GetTempPath(), "OstraPatchIdx_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(patchDir);
        File.WriteAllText(Path.Combine(patchDir, Patcher.MarkerFile), "{}");
        try
        {
            var patch = new ModEntry
            {
                Raw = Patcher.FolderName, Kind = EntryKind.Local, Name = Patcher.FolderName,
                Dir = patchDir, DisplayName = "Ostrasort Patch",
            };
            patch.DataFiles.Add("loot/loot.json");
            var src = Mod("ModA", dataFiles: new[] { "loot/loot.json" });
            var a = With(src, patch);
            Assert.True(patch.IsPatch);   // fixture sanity

            var issue = Assert.Single(Collect(a, "#Error# bad entry in loot/loot.json"));
            Assert.Null(issue.Mod);                       // ambiguous, not pinned on ModA
            Assert.Equal(2, issue.Candidates.Count);
        }
        finally { Directory.Delete(patchDir, recursive: true); }
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
            LogCorrelation.Annotate(a, [log]);
            // log issues live apart from the structural warnings so the health card
            // can drop them on dismissal, but every report still prints both
            Assert.Contains(a.LogWarnings, w => w.Contains("Evil Mod") && w.Contains("last game launch"));
            Assert.Empty(a.Warnings);
            Assert.Contains(a.AllWarnings, w => w.Contains("Evil Mod"));
            Assert.NotEmpty(mod.LogNotes);
        }
        finally { File.Delete(log); }
    }

    [Fact]
    public void DataError_NeverBlamesADisabledMod()
    {
        var off = Mod("Turned Off", dataFiles: new[] { "condowners/evil.json" });
        off.Disabled = true;
        var issue = Assert.Single(Collect(With(off), "#Error# Failed to parse condowners/evil.json"));
        Assert.Null(issue.Mod);
        Assert.Empty(issue.Candidates);
    }

    [Fact]
    public void DataError_InAFileCoreAlsoShips_IsNotPinnedOnTheMod()
    {
        var a = With(Mod("Overrider", dataFiles: new[] { "items/items.json" }));
        a.CoreDataFiles.Add("items/items.json");
        var issue = Assert.Single(Collect(a, "#Error# bad entry in items/items.json"));
        Assert.Null(issue.Mod);
        Assert.Equal("Overrider", Assert.Single(issue.Candidates).DisplayName);
    }

    [Fact]
    public void Annotate_SkipsAPlayerLogAnotherInstallWrote()
    {
        var a = WithEvilMod();
        var log = Path.Combine(Path.GetTempPath(), "OstraLog_" + Guid.NewGuid().ToString("N") + ".log");
        File.WriteAllText(log, "Mono path[0] = 'D:/Games/Ostranauts/Ostranauts_Data/Managed'\n" + EvilLine + "\n");
        try
        {
            LogCorrelation.Annotate(a, [log], gameRoot: @"C:\Program Files (x86)\Steam\steamapps\common\Ostranauts");
            Assert.Empty(a.LogWarnings);

            LogCorrelation.Annotate(a, [log], gameRoot: @"D:\Games\Ostranauts");
            Assert.NotEmpty(a.LogWarnings);
        }
        finally { File.Delete(log); }
    }

    // ------------------------------------------------------------ dismissal ---

    private static Analysis WithEvilMod() =>
        With(Mod("Evil Mod", dataFiles: new[] { "condowners/evil.json" }));

    private const string EvilLine = "#Error# Failed to parse condowners/evil.json";

    [Fact]
    public void Fingerprint_IsStableWhileTheLogIsUntouched()
    {
        // a dismissal has to survive every rescan, or it would clear itself
        var log = Path.Combine(Path.GetTempPath(), "OstraLog_" + Guid.NewGuid().ToString("N") + ".log");
        File.WriteAllText(log, EvilLine);
        try
        {
            var first = WithEvilMod();
            LogCorrelation.Annotate(first, [log]);
            var second = WithEvilMod();
            LogCorrelation.Annotate(second, [log]);

            Assert.NotNull(first.LogFingerprint);
            Assert.Equal(first.LogFingerprint, second.LogFingerprint);
        }
        finally { File.Delete(log); }
    }

    [Fact]
    public void Fingerprint_ChangesWhenTheGameRewritesTheLog()
    {
        // the point of "until it happens again": the game overwrites Player.log at
        // every launch, so the SAME error next time is a fresh occurrence
        var log = Path.Combine(Path.GetTempPath(), "OstraLog_" + Guid.NewGuid().ToString("N") + ".log");
        File.WriteAllText(log, EvilLine);
        try
        {
            var first = WithEvilMod();
            LogCorrelation.Annotate(first, [log]);

            File.SetLastWriteTimeUtc(log, DateTime.UtcNow.AddMinutes(5));   // a later launch
            var second = WithEvilMod();
            LogCorrelation.Annotate(second, [log]);

            Assert.NotEqual(first.LogFingerprint, second.LogFingerprint);
        }
        finally { File.Delete(log); }
    }

    [Fact]
    public void Fingerprint_IsNullWhenTheLogsReportedNothing()
    {
        var log = Path.Combine(Path.GetTempPath(), "OstraLog_" + Guid.NewGuid().ToString("N") + ".log");
        File.WriteAllText(log, "all fine here");
        try
        {
            var a = WithEvilMod();
            LogCorrelation.Annotate(a, [log]);
            Assert.Null(a.LogFingerprint);   // nothing to dismiss
        }
        finally { File.Delete(log); }
    }
}

/// <summary>The persisted "stop showing me the last launch's problems" preference.</summary>
public class LogDismissalTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "OstraDism_" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    private static GameEnv Env(string root) => new()
    {
        GameRoot = root, DiscoveredVia = "test",
        CoreDataDir = Path.Combine(root, "data"), ModsDir = Path.Combine(root, "Mods"),
    };

    private static Analysis Fp(string? fingerprint) =>
        new() { Registered = new(), LogFingerprint = fingerprint };

    [Fact]
    public void DismissedSetIsRememberedAcrossRuns()
    {
        var env = Env(@"C:\game-one");
        var a = Fp("ABC123");

        var list = new LogDismissal(_path);
        Assert.False(list.IsDismissed(env, a));
        list.Dismiss(env, a);

        Assert.True(new LogDismissal(_path).IsDismissed(env, a));
    }

    [Fact]
    public void ADifferentSetOfIssuesIsNotDismissed()
    {
        // the same problem happening again reads as a new fingerprint, so the alarm
        // comes back rather than staying hidden for good
        var env = Env(@"C:\game-one");
        var list = new LogDismissal(_path);
        list.Dismiss(env, Fp("ABC123"));

        Assert.False(list.IsDismissed(env, Fp("DEF456")));
    }

    [Fact]
    public void OneInstallNeverDismissesAnother()
    {
        var list = new LogDismissal(_path);
        list.Dismiss(Env(@"C:\game-one"), Fp("ABC123"));

        Assert.False(list.IsDismissed(Env(@"D:\game-two"), Fp("ABC123")));
    }

    [Fact]
    public void NothingToDismissIsANoOp()
    {
        var env = Env(@"C:\game-one");
        var list = new LogDismissal(_path);
        list.Dismiss(env, Fp(null));

        Assert.False(list.IsDismissed(env, Fp(null)));
        Assert.False(File.Exists(_path));
    }
}
