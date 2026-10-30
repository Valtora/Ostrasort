using System.IO;
using Ostrasort;
using Xunit;

namespace Ostrasort.Tests;

public class ModListSharingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "OstraShare_" + Guid.NewGuid().ToString("N"));
    private readonly GameEnv _env;

    public ModListSharingTests()
    {
        var mods = Path.Combine(_root, "game", "Ostranauts_Data", "Mods");
        Directory.CreateDirectory(Path.Combine(mods, "MyLocal"));
        Directory.CreateDirectory(Path.Combine(_root, "ws", "111"));
        var plug = Path.Combine(_root, "game", "BepInEx", "plugins", "Author-DataMod");
        Directory.CreateDirectory(plug);
        File.WriteAllText(Path.Combine(plug, "mod_info.json"), "[{}]");
        _env = new GameEnv
        {
            GameRoot = Path.Combine(_root, "game"), DiscoveredVia = "test", CoreDataDir = Path.Combine(_root, "core"),
            ModsDir = mods, WorkshopContentDir = Path.Combine(_root, "ws"), InstalledVersion = "1.0.1.8",
        };
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private Profile SampleProfile() => new()
    {
        Name = "Mine",
        Entries = new()
        {
            new("core", "core"),
            new(Path.Combine(_root, "ws", "111"), "Workshop One"),
            new("MyLocal|edit", "My Local"),
            new(Path.Combine(_env.BepInExDir, "plugins", "Author-DataMod") + "|disabled", "Data Mod"),
        },
    };

    [Fact]
    public void Code_RoundTrips_AndMatchesByIdAndFolder()
    {
        var a = new Analysis { Registered = new() };
        var list = ModListSharing.FromProfile(SampleProfile(), _env, a);
        var code = ModListSharing.ToCode(list);
        Assert.StartsWith(ModListSharing.CodePrefix, code);
        Assert.DoesNotContain('\n', code);

        var back = ModListSharing.Parse(code)!;
        Assert.Equal(new[] { "111", null, null }, back.Mods.Select(m => m.WorkshopId));
        Assert.True(back.Mods[2].Plugins && back.Mods[2].Disabled);

        var (profile, missing) = ModListSharing.Resolve(back, _env, a, "Imported");
        Assert.Empty(missing);
        Assert.Equal(new[] { "core", Path.Combine(_root, "ws", "111"), "MyLocal|edit",
                             Path.Combine(_env.BepInExDir, "plugins", "Author-DataMod") + "|disabled" }, profile.Raws);
    }

    [Fact]
    public void File_Parses_AndReportsWhatIsMissing()
    {
        var json = ModListSharing.ToJson(new SharedList
        {
            Name = "Friend's", Mods = { new() { WorkshopId = "111", Name = "One" }, new() { WorkshopId = "999", Name = "Not Subscribed" },
                                        new() { Folder = "TheirLocal", Name = "Their Local" } },
        });

        var (profile, missing) = ModListSharing.Resolve(ModListSharing.Parse(json)!, _env, new Analysis { Registered = new() }, "x");

        Assert.Equal(2, profile.Entries.Count);
        Assert.Equal(new[] { "Not Subscribed", "Their Local" }, missing.Select(m => m.Name));
    }

    [Fact]
    public void LocalCopyOfAPublishedMod_MatchesItsWorkshopId()
    {
        var a = new Analysis { Registered = new() { new ModEntry { Raw = "MyLocal|edit", Kind = EntryKind.Local, Name = "MyLocal", Dir = "x", PublishedId = "555" } } };
        var list = new SharedList { Mods = { new() { WorkshopId = "555", Name = "Published" } } };
        Assert.Equal("MyLocal|edit", ModListSharing.Resolve(list, _env, a, "x").Profile.Entries[1].Raw);
    }

    [Theory]
    [InlineData("https://steamcommunity.com/sharedfiles/filedetails/?id=3777213341", "3777213341")]
    [InlineData("3777213341", "3777213341")]
    [InlineData("steam://url/CommunityFilePage/3777213341", "3777213341")]
    [InlineData("hello", null)]
    public void ParseId(string text, string? id) => Assert.Equal(id, Workshop.ParseId(text));

    [Fact]
    public void Collection_ChildrenComeBackInSortOrder()
    {
        var json = """
            {"response":{"result":1,"resultcount":1,"collectiondetails":[{"publishedfileid":"1","result":1,"children":[
              {"publishedfileid":"30","sortorder":2,"filetype":0},
              {"publishedfileid":"10","sortorder":0,"filetype":0},
              {"publishedfileid":"20","sortorder":1,"filetype":2}]}]}}
            """;
        Assert.Equal(new[] { ("10", false), ("20", true), ("30", false) }, Workshop.ParseCollection(json));
        Assert.Null(Workshop.ParseCollection("""{"response":{"collectiondetails":[{"publishedfileid":"1","result":9}]}}"""));
    }

    [Fact]
    public void Garbage_IsNotAList()
    {
        Assert.Null(ModListSharing.Parse("not a list"));
        Assert.Null(ModListSharing.Parse(ModListSharing.CodePrefix + "!!!"));
    }
}
