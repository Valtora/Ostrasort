using System.Text.Json;
using Ostrasort;
using Xunit;

namespace Ostrasort.Tests;

/// <summary>
/// In-memory tests for the Installations store. Deliberately never calls
/// Load()/Save() (those hit the real %LOCALAPPDATA% file); the persistence
/// contract is checked with an explicit JsonSerializer round-trip instead.
/// </summary>
public class InstallationsTests
{
    [Fact]
    public void Upsert_AddsThenReplacesByName_CaseInsensitive()
    {
        var s = new InstallationStore();
        s.Upsert(new Installation { Name = "Main", GameRoot = @"C:\a" });
        s.Upsert(new Installation { Name = "main", GameRoot = @"C:\b" });   // same name, different case

        Assert.Single(s.Items);
        Assert.Equal(@"C:\b", s.Find("MAIN")!.GameRoot);
    }

    [Fact]
    public void Remove_DeletesAndClearsActiveWhenItMatched()
    {
        var s = new InstallationStore { Active = "FFU" };
        s.Upsert(new Installation { Name = "FFU" });
        s.Upsert(new Installation { Name = "Vanilla" });

        s.Remove("ffu");

        Assert.Null(s.Find("FFU"));
        Assert.Null(s.Active);              // active pointed at the removed one -> cleared
        Assert.NotNull(s.Find("Vanilla"));
    }

    [Fact]
    public void Installation_NormalisesBlankOverridesToNull_AndTrims()
    {
        var blank = new Installation { Name = "x", GameRoot = "   ", ModsDir = "" };
        Assert.Null(blank.Game);
        Assert.Null(blank.Mods);

        var set = new Installation { Name = "y", GameRoot = @"  D:\game  ", ModsDir = @"D:\mods" };
        Assert.Equal(@"D:\game", set.Game);   // trimmed
        Assert.Equal(@"D:\mods", set.Mods);
    }

    [Fact]
    public void JsonRoundTrip_PreservesItemsAndActive()
    {
        var s = new InstallationStore { Active = "B" };
        s.Items.Add(new Installation { Name = "A", GameRoot = @"C:\g", ModsDir = @"D:\m" });
        s.Items.Add(new Installation { Name = "B" });   // both overrides null => auto-detect

        var back = JsonSerializer.Deserialize<InstallationStore>(JsonSerializer.Serialize(s))!;

        Assert.Equal("B", back.Active);
        Assert.Equal(2, back.Items.Count);
        Assert.Equal(@"C:\g", back.Find("A")!.GameRoot);
        Assert.Equal(@"D:\m", back.Find("A")!.ModsDir);
        Assert.Null(back.Find("B")!.GameRoot);
    }
}
