using System.IO;
using Ostrasort;
using Xunit;

namespace Ostrasort.Tests;

/// <summary>File-based scanner tests against a throwaway fixture folder.</summary>
public class ScannerTests : IDisposable
{
    private readonly string _root;

    public ScannerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "OstraScan_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "core"));
        Directory.CreateDirectory(Path.Combine(_root, "Mods"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private GameEnv Env() => new()
    {
        GameRoot = _root,
        DiscoveredVia = "test",
        CoreDataDir = Path.Combine(_root, "core"),
        ModsDir = Path.Combine(_root, "Mods"),
    };

    private ModEntry MakeMod(string name)
    {
        var dir = Path.Combine(_root, "Mods", name);
        Directory.CreateDirectory(dir);
        return new ModEntry { Raw = name, Kind = EntryKind.Local, Name = name, Dir = dir };
    }

    private static void WriteJson(string modDir, string relPath, string json)
    {
        var path = Path.Combine(modDir, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    [Fact]
    public void Scan_ExpandsConditionsSimpleIntoTheConditionsNamespace()
    {
        var mod = MakeMod("Simple");
        WriteJson(mod.Dir!, @"data\conditions_simple\mine.json", """
            [{"strName":"My Simple Conditions","aValues":[
              "CondAlpha","Alpha","desc","0","0","Neutral","false",
              "CondBeta","Beta","desc","0","0","Neutral","false"
            ]}]
            """);

        new Scanner(Env(), useCoreCache: false).Scan(mod);

        // the game's ParseConditionsSimple writes these into dictConds - so they
        // claim the CONDITIONS namespace, not just the container
        Assert.True(mod.Claims.ContainsKey(("conditions", "CondAlpha")));
        Assert.True(mod.Claims.ContainsKey(("conditions", "CondBeta")));
        Assert.True(mod.Claims.ContainsKey(("conditions_simple", "My Simple Conditions")));
        Assert.Contains("CondAlpha", mod.SimpleConditionNames);
    }

    [Fact]
    public void Scan_SkipsFilesMatchingIgnorePatterns()
    {
        var mod = MakeMod("Ignored");
        WriteJson(mod.Dir!, @"data\loot\keep.json", """[{"strName":"Keep","aLoots":["a=1x1"]}]""");
        WriteJson(mod.Dir!, @"data\loot\LA_skip.json", """[{"strName":"Skip","aLoots":["b=1x1"]}]""");

        new Scanner(Env(), new[] { "LA_" }, useCoreCache: false).Scan(mod);

        Assert.True(mod.Claims.ContainsKey(("loot", "Keep")));
        Assert.False(mod.Claims.ContainsKey(("loot", "Skip")));   // the game skips this file entirely
        Assert.Contains(mod.IgnoredFiles, f => f.Pattern == "LA_");
    }

    [Fact]
    public void IndexCore_CacheHitMatchesFreshScan_AndInvalidatesOnChange()
    {
        var env = Env();
        WriteJson(env.CoreDataDir, @"conditions\a.json", """[{"strName":"CondA"}]""");
        WriteJson(env.CoreDataDir, @"loot\p.json", """[{"strName":"PoolP","aLoots":["x=1x1"]}]""");

        var fresh = new Scanner(env, useCoreCache: true);
        fresh.IndexCore();                                   // cold: parses + saves the cache
        var cached = new Scanner(env, useCoreCache: true);
        cached.IndexCore();                                  // warm: loads the cache
        Assert.Equal(fresh.CoreIndex, cached.CoreIndex);
        Assert.Equal(fresh.CoreTypes, cached.CoreTypes);

        WriteJson(env.CoreDataDir, @"conditions\b.json", """[{"strName":"CondB (added later)"}]""");
        var rescanned = new Scanner(env, useCoreCache: true);
        rescanned.IndexCore();                               // fingerprint changed -> full rescan
        Assert.Contains(("conditions", "CondB (added later)"), rescanned.CoreIndex);
    }

    [Fact]
    public void IndexCore_AppliesIgnorePatternsOnTopOfTheCache()
    {
        var env = Env();
        WriteJson(env.CoreDataDir, @"loot\keep.json", """[{"strName":"Keep"}]""");
        WriteJson(env.CoreDataDir, @"loot\LA_big.json", """[{"strName":"Big"}]""");

        new Scanner(env, useCoreCache: true).IndexCore();    // prime the (pattern-free) cache

        var filtered = new Scanner(env, new[] { "LA_" }, useCoreCache: true);
        filtered.IndexCore();
        Assert.Contains(("loot", "Keep"), filtered.CoreIndex);
        Assert.DoesNotContain(("loot", "Big"), filtered.CoreIndex);
        Assert.Contains(filtered.IgnoredCoreFiles, f => f.Pattern == "LA_");
    }

    [Fact]
    public void Scan_AcceptsJsonComments_ButStillFlagsTrailingCommas()
    {
        var mod = MakeMod("Comments");
        // // and /* */ comments are game-legal (core ships them, e.g. tokens/verbs.json) - no warning
        WriteJson(mod.Dir!, @"data\items\commented.json", """
            [
              // a line comment, like the game's own data files carry
              {"strName":"ItmCommented"}  /* and a block comment */
            ]
            """);
        // a trailing comma is NOT game-legal - the loader errors on it, so keep flagging it
        WriteJson(mod.Dir!, @"data\items\trailing.json", "[{\"strName\":\"ItmTrailing\"},]");

        new Scanner(Env(), useCoreCache: false).Scan(mod);

        Assert.True(mod.Claims.ContainsKey(("items", "ItmCommented")));   // parsed despite comments
        Assert.True(mod.Claims.ContainsKey(("items", "ItmTrailing")));    // parsed leniently too
        Assert.DoesNotContain(mod.JsonErrors, e => e.Contains("commented.json"));
        Assert.Contains(mod.JsonErrors, e => e.Contains("trailing.json") && e.Contains("trailing comma"));
    }

    [Fact]
    public void Scan_ReadsBFFUHintFromModInfo_AsAnFfuSignal()
    {
        var mod = MakeMod("Fragment");
        WriteJson(mod.Dir!, "mod_info.json", """[{"strName":"Fragment Mod","bFFU":true}]""");
        // a pure field-merge fragment: partial object, no auto-detectable FFU marker
        WriteJson(mod.Dir!, @"data\condowners\frag.json", """[{"strName":"AABarTechnoLowPass","fMass":9.0}]""");

        new Scanner(Env(), useCoreCache: false).Scan(mod);

        Assert.True(mod.UsesElasticApi);                            // feeds FFU classification (AfterFFU)
        Assert.Contains(mod.FfuSignals, s => s.Contains("bFFU"));
    }
}
