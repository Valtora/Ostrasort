using System.IO;
using Ostrasort;
using Xunit;
using static Ostrasort.Tests.TestData;

namespace Ostrasort.Tests;

// ---------------------------------------------------------------------------
// Autoload.Meta.toml parsing
// ---------------------------------------------------------------------------

public class AutoloadMetaTests
{
    private static AutoloadMeta? Parse(string content)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ostrasort-meta-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, AutoloadMeta.FileName), content);
            return AutoloadMeta.Read(dir);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Read_NullWhenNoFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ostrasort-meta-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try { Assert.Null(AutoloadMeta.Read(dir)); }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Read_RealTemplate_GroupAndComments()
    {
        var meta = Parse("""
            # Autoload Meta file identifier for validation
            FileType="AUTOLOAD.META"

            # This instructs the loader which LoadGroup your mod belongs to.
            LoadGroup="FFUCore"

            [dependencies]
              # List dependencies here as keys with optional versions as values.
            """);
        Assert.NotNull(meta);
        Assert.Equal(FfuLoadGroup.FFUCore, meta!.Group);
        Assert.Empty(meta.Dependencies);
        Assert.Empty(meta.Problems);
    }

    [Fact]
    public void Read_QuotedDependencyNamesWithSpaces()
    {
        var meta = Parse("""
            LoadGroup="AfterFFU"
            [dependencies]
            "Minor Fixes Plus" = "ANY"
            NoSpacesMod = "1.0.0"
            """);
        Assert.Equal(FfuLoadGroup.AfterFFU, meta!.Group);
        Assert.Equal("ANY", meta.Dependencies["Minor Fixes Plus"]);
        Assert.Equal("1.0.0", meta.Dependencies["NoSpacesMod"]);
    }

    [Fact]
    public void Read_UnknownGroup_IsProblemNotCrash()
    {
        var meta = Parse("""LoadGroup="Sideways" """);
        Assert.Null(meta!.Group);
        Assert.Contains(meta.Problems, p => p.Contains("Sideways"));
    }
}

// ---------------------------------------------------------------------------
// Install-level detection: only the autoloader DLL is a rival; FFU is supported
// ---------------------------------------------------------------------------

public class FfuContextTests
{
    private static GameEnv EnvAt(string root) => new()
    {
        GameRoot = root,
        DiscoveredVia = "test",
        CoreDataDir = Path.Combine(root, "Ostranauts_Data", "StreamingAssets", "data"),
        ModsDir = Path.Combine(root, "Ostranauts_Data", "Mods"),
    };

    private static void Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
    }

    private static void WithRoot(Action<string> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "ostrasort-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { body(root); }
        finally { try { Directory.Delete(root, true); } catch { /* best-effort cleanup */ } }
    }

    [Fact]
    public void Detect_NullWhenNoBepInEx() =>
        WithRoot(root => Assert.Null(FfuContext.Detect(EnvAt(root))));

    [Fact]
    public void Detect_NullForOrdinaryWorkshopPlugin() => WithRoot(root =>
    {
        Touch(Path.Combine(root, "BepInEx", "plugins", "SomeMod", "SomeMod.dll"));
        Assert.Null(FfuContext.Detect(EnvAt(root)));
    });

    [Fact]
    public void Detect_FfuMonoModPatch_IsFrameworkNotRival() => WithRoot(root =>
    {
        Touch(Path.Combine(root, "BepInEx", "monomod", "Assembly-CSharp.FFU_BR.mm.dll"));
        var ctx = FfuContext.Detect(EnvAt(root));
        Assert.NotNull(ctx);
        Assert.True(ctx!.FrameworkPresent);
        Assert.False(ctx.AutoloaderActive);          // FFU alone never gates writes
        Assert.False(ctx.MonoModLoaderPresent);      // no MonoMod.dll in core -> hygiene case
    });

    [Fact]
    public void Detect_AutoloaderPluginDll_IsRival() => WithRoot(root =>
    {
        Touch(Path.Combine(root, "BepInEx", "plugins", "OstraAutoloader", "Ostra.Autoloader.dll"));
        var ctx = FfuContext.Detect(EnvAt(root));
        Assert.NotNull(ctx);
        Assert.True(ctx!.AutoloaderActive);
    });

    [Fact]
    public void Detect_MetaFileAlone_IsInert() => WithRoot(root =>
    {
        // a meta file without the autoloader DLL is just ordering metadata -
        // it must NOT put Ostrasort into read-only mode (v0.9.0 got this wrong)
        Touch(Path.Combine(root, "BepInEx", "core", "BepInEx.dll"));
        Touch(Path.Combine(root, "BepInEx", "plugins", "Minor_Fixes_Plus", "Autoload.Meta.toml"));
        Touch(Path.Combine(root, "Ostranauts_Data", "Mods", "SomeFfuMod", "Autoload.Meta.toml"));
        Assert.Null(FfuContext.Detect(EnvAt(root)));
    });

    [Fact]
    public void Detect_MonoModLoaderFound() => WithRoot(root =>
    {
        Touch(Path.Combine(root, "BepInEx", "monomod", "Assembly-CSharp.FFU_BR.mm.dll"));
        Touch(Path.Combine(root, "BepInEx", "core", "MonoMod.dll"));
        Assert.True(FfuContext.Detect(EnvAt(root))!.MonoModLoaderPresent);
    });

    [Fact]
    public void DisableAutoloader_RenamesReversiblyAndClearsDetection() => WithRoot(root =>
    {
        var dll = Path.Combine(root, "BepInEx", "plugins", "OstraAutoloader", "Ostranauts.Autoloader.dll");
        Touch(dll);
        var ctx = FfuContext.Detect(EnvAt(root));
        Assert.True(ctx!.AutoloaderActive);
        Assert.Single(ctx.AutoloaderDlls);

        var renamed = FfuAnalysis.DisableAutoloader(ctx);

        var target = Assert.Single(renamed);
        Assert.Equal(dll + ".disabled", target);
        Assert.True(File.Exists(target));
        Assert.False(File.Exists(dll));
        Assert.Null(FfuContext.Detect(EnvAt(root)));   // no longer detected as a rival
    });
}

// ---------------------------------------------------------------------------
// Classification: what makes a mod part of the FFU block
// ---------------------------------------------------------------------------

public class FfuClassifyTests
{
    private static GameEnv TestEnv => MakeEnv();

    private static GameEnv MakeEnv(string? installedVersion = null) => new()
    {
        GameRoot = @"C:\nonexistent-ostrasort-test",
        DiscoveredVia = "test",
        CoreDataDir = @"C:\nonexistent-ostrasort-test\data",
        ModsDir = @"C:\nonexistent-ostrasort-test\Mods",
        InstalledVersion = installedVersion,
    };

    private static Analysis Analyze(params ModEntry[] mods)
    {
        var a = new Analysis { Registered = mods.ToList() };
        FfuAnalysis.Classify(TestEnv, a);
        return a;
    }

    private static AutoloadMeta Meta(FfuLoadGroup group, params string[] deps)
    {
        var m = new AutoloadMeta { Group = group, RawGroup = group.ToString() };
        foreach (var d in deps) m.Dependencies[d] = "ANY";
        return m;
    }

    [Fact]
    public void MetaGroup_SeedsFfu()
    {
        var m = Mod("SomeFfuMod");
        m.Meta = Meta(FfuLoadGroup.AfterFFU);
        Analyze(m);
        Assert.True(m.IsFfu);
        Assert.Equal(FfuLoadGroup.AfterFFU, m.FfuGroup);
    }

    [Fact]
    public void ElasticApi_SeedsFfu()
    {
        var m = Mod("SneakyElastic");
        m.UsesElasticApi = true;
        Analyze(m);
        Assert.True(m.IsFfu);
    }

    [Fact]
    public void MinorFixesPlus_IsCoreTierByName()
    {
        var m = Mod("Minor_Fixes_Plus");
        m.DisplayName = "Minor Fixes Plus";
        Analyze(m);
        Assert.True(m.IsFfu);
        Assert.Equal(FfuLoadGroup.FFUCore, m.FfuGroup);
    }

    [Fact]
    public void WithVanillaMeta_StaysOutOfFfuBlock()
    {
        var m = Mod("VanillaContent");
        m.Meta = Meta(FfuLoadGroup.WithVanilla);
        Analyze(m);
        Assert.False(m.IsFfu);
    }

    [Fact]
    public void DependencyOnFfuMod_Propagates()
    {
        var mfp = Mod("Minor_Fixes_Plus"); mfp.DisplayName = "Minor Fixes Plus";
        var dep = Mod("GunsPack");
        dep.Meta = Meta(FfuLoadGroup.WithVanilla);   // explicit opt-out is honoured
        var dep2 = Mod("ShipsPack");
        dep2.Meta = new AutoloadMeta();              // no group declared
        dep2.Meta.Dependencies["Minor Fixes Plus"] = "ANY";
        Analyze(mfp, dep, dep2);
        Assert.False(dep.IsFfu);
        Assert.True(dep2.IsFfu);
        Assert.Equal(FfuLoadGroup.AfterFFU, dep2.FfuGroup);
    }

    [Fact]
    public void PatchMod_ResolvesTargetFromDependency()
    {
        var target = Mod("CoolGuns"); target.UsesElasticApi = true;
        var patch = Mod("CoolGunsPatch"); patch.DisplayName = "CoolGuns Patch";
        patch.Meta = new AutoloadMeta { Group = FfuLoadGroup.AfterFFU, RawGroup = "AfterFFU" };
        patch.Meta.Dependencies["CoolGuns"] = "ANY";
        var a = Analyze(target, patch);

        Assert.True(patch.IsFfuPatch);
        Assert.Same(target, patch.FfuPatchTarget);
        Assert.Contains(a.Warnings, w => w.Contains("applies once"));
    }

    [Fact]
    public void FfuModsWithoutFramework_Warn()
    {
        var m = Mod("Orphan"); m.UsesElasticApi = true;
        var a = Analyze(m);
        Assert.NotNull(a.Ffu);                       // context created so the banner shows
        Assert.False(a.Ffu!.FrameworkPresent);
        Assert.Contains(a.Warnings, w => w.Contains("framework") && w.Contains("missing"));
    }

    [Fact]
    public void FfuGameVersionMismatch_WarnsLoudly()
    {
        // the exact failure seen live: FFU built for 0.15.1.0 on a 0.15.1.6 game
        // = broken main menu + NRE spam, with a clean-looking MonoMod log
        var mfp = Mod("Minor_Fixes_Plus");
        mfp.DisplayName = "Minor Fixes Plus";
        mfp.GameVersion = "0.15.1.0";
        var a = new Analysis { Registered = [mfp] };
        a.Ffu = new FfuContext { FrameworkPresent = true };

        FfuAnalysis.Classify(MakeEnv("0.15.1.6"), a);

        Assert.Contains(a.Warnings, w => w.Contains("FFU VERSION MISMATCH") && w.Contains("0.15.1.6"));
    }

    [Fact]
    public void FfuGameVersionMatch_NoMismatchWarning()
    {
        var mfp = Mod("Minor_Fixes_Plus");
        mfp.DisplayName = "Minor Fixes Plus";
        mfp.GameVersion = "0.15.1.6";
        var a = new Analysis { Registered = [mfp] };
        a.Ffu = new FfuContext { FrameworkPresent = true };

        FfuAnalysis.Classify(MakeEnv("0.15.1.6"), a);

        Assert.DoesNotContain(a.Warnings, w => w.Contains("FFU VERSION MISMATCH"));
    }
}

// ---------------------------------------------------------------------------
// Sort rules: the FFU block, Minor Fixes Plus first, deps, patch placement
// ---------------------------------------------------------------------------

public class FfuOrderTests
{
    private static ModEntry Core() =>
        new() { Raw = "core", Kind = EntryKind.Core, Name = "core", Dir = "core", Class = ModClass.Core };

    private static ModEntry Local(string name, bool ffu = false, FfuLoadGroup group = FfuLoadGroup.AfterFFU)
    {
        var m = new ModEntry
        {
            Raw = name, Kind = EntryKind.Local, Name = name, Dir = name,
        };
        m.DisplayName = name;
        m.Class = ModClass.DataOverride;
        m.IsFfu = ffu;
        m.FfuGroup = group;
        return m;
    }

    [Fact]
    public void FfuMods_MoveAfterNonFfuMods()
    {
        var a = new Analysis { Registered = [Core(), Local("FfuThing", ffu: true), Local("PlainData")] };
        a.BuildSuggestion();
        Assert.Equal(new[] { "core", "PlainData", "FfuThing" }, a.SuggestedOrder);
        Assert.True(a.OrderChanged);
    }

    [Fact]
    public void MinorFixesPlusTier_LeadsTheFfuBlock()
    {
        var mfp = Local("Minor_Fixes_Plus", ffu: true, group: FfuLoadGroup.FFUCore);
        var a = new Analysis { Registered = [Core(), Local("SomeFfuMod", ffu: true), mfp] };
        a.BuildSuggestion();
        Assert.Equal(new[] { "core", "Minor_Fixes_Plus", "SomeFfuMod" }, a.SuggestedOrder);
    }

    [Fact]
    public void FfuDependencies_LoadBeforeDependents()
    {
        var lib = Local("FfuLib", ffu: true);
        var user = Local("FfuUser", ffu: true);
        user.Meta = new AutoloadMeta { Group = FfuLoadGroup.AfterFFU, RawGroup = "AfterFFU" };
        user.Meta.Dependencies["FfuLib"] = "ANY";
        var a = new Analysis { Registered = [Core(), user, lib] };
        a.BuildSuggestion();
        Assert.Equal(new[] { "core", "FfuLib", "FfuUser" }, a.SuggestedOrder);
    }

    [Fact]
    public void FfuPatchMod_SitsRightAfterItsTarget()
    {
        var target = Local("CoolGuns", ffu: true);
        var other = Local("OtherFfu", ffu: true);
        var patch = Local("CoolGunsPatch", ffu: true);
        patch.IsFfuPatch = true;
        patch.FfuPatchTarget = target;
        var a = new Analysis { Registered = [Core(), patch, other, target] };
        a.BuildSuggestion();
        var order = a.SuggestedOrder;
        Assert.Equal(order.IndexOf("CoolGuns") + 1, order.IndexOf("CoolGunsPatch"));
    }

    [Fact]
    public void GeneratedPatch_ClosesTheNonFfuBlock()
    {
        // the Ostrasort Patch is identified via its marker file - build a real one
        var root = Path.Combine(Path.GetTempPath(), "ostrasort-test-" + Guid.NewGuid().ToString("N"));
        var patchDir = Path.Combine(root, Patcher.FolderName);
        Directory.CreateDirectory(patchDir);
        try
        {
            File.WriteAllText(Path.Combine(patchDir, Patcher.MarkerFile), "{}");
            var patch = new ModEntry
            {
                Raw = Patcher.FolderName, Kind = EntryKind.Local, Name = Patcher.FolderName, Dir = patchDir,
            };
            patch.Class = ModClass.DataOverride;

            var a = new Analysis { Registered = [Core(), patch, Local("PlainData"), Local("FfuThing", ffu: true)] };
            a.BuildSuggestion();
            Assert.Equal(new[] { "core", "PlainData", Patcher.FolderName, "FfuThing" }, a.SuggestedOrder);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void ValidateOrder_FlagsNonFfuAfterFfu()
    {
        var issues = Analysis.ValidateOrder([Core(), Local("FfuThing", ffu: true), Local("PlainData")]);
        Assert.Contains(issues, i => i.Contains("non-FFU"));
    }

    [Fact]
    public void ValidateOrder_FlagsDependencyAfterDependent()
    {
        var lib = Local("FfuLib", ffu: true);
        var user = Local("FfuUser", ffu: true);
        user.Meta = new AutoloadMeta { Group = FfuLoadGroup.AfterFFU, RawGroup = "AfterFFU" };
        user.Meta.Dependencies["FfuLib"] = "ANY";
        var issues = Analysis.ValidateOrder([Core(), user, lib]);
        Assert.Contains(issues, i => i.Contains("FfuLib"));
    }
}

// ---------------------------------------------------------------------------
// Remove FFU Core: reversible parking + unregistration
// ---------------------------------------------------------------------------

public class FfuRemovalTests
{
    [Fact]
    public void RemoveFfuCore_ParksFilesUnregistersAndClearsDetection()
    {
        var root = Path.Combine(Path.GetTempPath(), "ostrasort-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var dll = Path.Combine(root, "BepInEx", "monomod", "Assembly-CSharp.FFU_BR.mm.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(dll)!);
            File.WriteAllText(dll, "");

            var mfpDir = Path.Combine(root, "BepInEx", "plugins", "Minor_Fixes_Plus");
            Directory.CreateDirectory(Path.Combine(mfpDir, "data", "loot"));
            File.WriteAllText(Path.Combine(mfpDir, "mod_info.json"),
                """[{"strName":"Minor Fixes Plus","strAuthor":"t","strGameVersion":"0.15.1.0"}]""");
            File.WriteAllText(Path.Combine(mfpDir, "data", "loot", "x.json"),
                """[{"strName":"SomePool","aLoots":["--ADD--","Itm=1.0x1"]}]""");

            var modsDir = Path.Combine(root, "Ostranauts_Data", "Mods");
            Directory.CreateDirectory(modsDir);
            File.WriteAllText(Path.Combine(modsDir, "loading_order.json"),
                $$"""[{"strName":"Mod Loading Order","aLoadOrder":["core","{{mfpDir.Replace("\\", "\\\\")}}"],"aIgnorePatterns":[]}]""");

            var env = new GameEnv
            {
                GameRoot = root,
                DiscoveredVia = "test",
                CoreDataDir = Path.Combine(root, "Ostranauts_Data", "StreamingAssets", "data"),
                ModsDir = modsDir,
                InstalledVersion = "0.15.1.6",
            };

            var state = Engine.Analyze(env);
            Assert.NotNull(state.Analysis.Ffu);
            Assert.Contains(state.Analysis.Warnings, w => w.Contains("FFU VERSION MISMATCH"));

            var removal = FfuAnalysis.RemoveFfuCore(env, state.Analysis.Ffu!, state.Analysis);

            Assert.Contains(removal.Renamed, p => p.EndsWith(".mm.dll.disabled"));
            Assert.Contains(removal.Renamed, p => p.EndsWith("Minor_Fixes_Plus.disabled"));
            Assert.Single(removal.Unregistered);
            Assert.False(File.Exists(dll));
            Assert.False(Directory.Exists(mfpDir));

            // a fresh pass sees a clean, FFU-free install: entry gone, parked
            // folder not re-discovered, no FFU context at all
            var after = Engine.Analyze(env);
            Assert.Single(after.Analysis.Registered);          // just core
            Assert.Empty(after.Analysis.UnregisteredLocal);
            Assert.Null(after.Analysis.Ffu);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}

// ---------------------------------------------------------------------------
// Merge semantics: FFU fragments touch only the fields they carry
// ---------------------------------------------------------------------------

public class FfuMergeTests
{
    [Fact]
    public void FragmentFromFfuMod_AbsentFieldIsNotARemoval()
    {
        var full = Mod("FullOverride");
        var frag = Mod("FfuFragment");
        frag.IsFfu = true;

        var core = Obj("""{"strName":"X","a":1,"b":2}""");
        var plan = ObjectMerge.Build(Coll("condowners", "X", full, frag), core,
            Versions((full, """{"strName":"X","a":1,"b":3}"""),   // full copy, changed b
                     (frag, """{"strName":"X","a":5}""")));        // fragment: only touches a

        Assert.NotNull(plan);
        // 'b' must have exactly one changer (the full override) - the fragment's
        // missing 'b' means "untouched", not "removed"
        var b = plan!.Fields.Single(f => f.Token == "b");
        var opt = Assert.Single(b.Options);
        Assert.Equal("FullOverride", opt.SourceLabel);
        var aField = plan.Fields.Single(f => f.Token == "a");
        Assert.Equal("FfuFragment", Assert.Single(aField.Options).SourceLabel);
    }

    [Fact]
    public void FfuFieldMerge_TreatsEveryVersionAsFragment()
    {
        var m1 = Mod("LegacyA");
        var m2 = Mod("LegacyB");
        var core = Obj("""{"strName":"X","a":1,"b":2}""");
        var plan = ObjectMerge.Build(Coll("condowners", "X", m1, m2), core,
            Versions((m1, """{"strName":"X","a":9}"""),
                     (m2, """{"strName":"X","a":1,"b":2,"c":4}""")),
            ffuFieldMerge: true);

        Assert.NotNull(plan);
        Assert.DoesNotContain(plan!.Fields, f => f.Token == "b");   // absent from m1 = untouched
        Assert.Contains(plan.Fields, f => f.Token == "a");
        Assert.Contains(plan.Fields, f => f.Token == "c");
    }

    [Fact]
    public void WithoutFfu_AbsentFieldStillMeansRemoval()
    {
        var m1 = Mod("LegacyA");
        var m2 = Mod("LegacyB");
        var core = Obj("""{"strName":"X","a":1,"b":2}""");
        var plan = ObjectMerge.Build(Coll("condowners", "X", m1, m2), core,
            Versions((m1, """{"strName":"X","a":1}"""),             // legacy full override that dropped b
                     (m2, """{"strName":"X","a":1,"b":2,"c":4}""")));

        Assert.Contains(plan!.Fields, f => f.Token == "b");          // removal is still a change
    }
}

// ---------------------------------------------------------------------------
// Scanner: elastic-API markers, meta files, command-driven loot claims
// ---------------------------------------------------------------------------

public class FfuScannerTests
{
    [Fact]
    public void Scan_DetectsElasticMarkersAndMeta()
    {
        var root = Path.Combine(Path.GetTempPath(), "ostrasort-test-" + Guid.NewGuid().ToString("N"));
        var modDir = Path.Combine(root, "Ostranauts_Data", "Mods", "FfuMod");
        try
        {
            Directory.CreateDirectory(Path.Combine(modDir, "data", "condowners"));
            Directory.CreateDirectory(Path.Combine(modDir, "data", "loot"));
            File.WriteAllText(Path.Combine(modDir, "mod_info.json"), """
                [{"strName":"FfuMod","strAuthor":"t","strGameVersion":"1",
                  "removeIds":{"cooverlays":["OutfitEVA03","OutfitEVA03Off"]},
                  "changesMap":{"OutfitEVA01":{"Recover_Missing":[]}}}]
                """);
            File.WriteAllText(Path.Combine(modDir, AutoloadMeta.FileName), """
                LoadGroup="AfterFFU"
                [dependencies]
                "Minor Fixes Plus" = "ANY"
                """);
            File.WriteAllText(Path.Combine(modDir, "data", "condowners", "frag.json"),
                """[{"strName":"NewSuit","strReference":"OutfitEVA01","jsonPI":"EVASuit05"}]""");
            File.WriteAllText(Path.Combine(modDir, "data", "loot", "pool.json"),
                """[{"strName":"StnKioskPool","aLoots":["--ADD--","ItmThing=1.0x1"]}]""");

            var env = new GameEnv
            {
                GameRoot = root,
                DiscoveredVia = "test",
                CoreDataDir = Path.Combine(root, "Ostranauts_Data", "StreamingAssets", "data"),
                ModsDir = Path.Combine(root, "Ostranauts_Data", "Mods"),
            };
            var mod = ModEntry.Parse("FfuMod", env);
            new Scanner(env).Scan(mod);

            Assert.True(mod.UsesElasticApi);
            Assert.Equal(2, mod.RemoveIds.Count);
            Assert.True(mod.HasChangesMap);
            Assert.Equal(FfuLoadGroup.AfterFFU, mod.Meta?.Group);
            Assert.True(mod.Meta?.Dependencies.ContainsKey("Minor Fixes Plus"));
            Assert.Contains(("loot", "StnKioskPool"), mod.FfuArrayEditClaims);
            Assert.Null(mod.Claims[("loot", "StnKioskPool")]);      // command edit = non-comparable pool
            Assert.True(mod.Claims.ContainsKey(("condowners", "NewSuit")));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
