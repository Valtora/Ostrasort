using System.IO;
using Ostrasort;
using Xunit;

namespace Ostrasort.Tests;

/// <summary>The side-by-side collision detail model, built from on-disk objects.</summary>
public class CollisionDetailTests : IDisposable
{
    private readonly string _root;
    private readonly GameEnv _env;

    public CollisionDetailTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "OstraDetail_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "core"));
        Directory.CreateDirectory(Path.Combine(_root, "Mods"));
        _env = new GameEnv { GameRoot = _root, DiscoveredVia = "test",
            CoreDataDir = Path.Combine(_root, "core"), ModsDir = Path.Combine(_root, "Mods") };
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static void Write(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    private ModEntry Mod(string name, string type, string json)
    {
        var dir = Path.Combine(_root, "Mods", name);
        Write(Path.Combine(dir, "data", type, "d.json"), json);
        return new ModEntry { Raw = name, Kind = EntryKind.Local, Name = name, Dir = dir, DisplayName = name };
    }

    [Fact]
    public void Build_WithVanilla_MarksChangedAndContestedFields()
    {
        Write(Path.Combine(_env.CoreDataDir, "conditions", "c.json"),
            """[{"strName":"X","a":1,"b":1,"c":1}]""");
        var mA = Mod("ModA", "conditions", """[{"strName":"X","a":2,"b":1,"c":1}]""");   // changes a
        var mB = Mod("ModB", "conditions", """[{"strName":"X","a":9,"b":1,"c":1}]""");   // changes a differently
        var col = new Collision { Type = "conditions", ObjName = "X", Claimants = new() { mA, mB } };

        var model = CollisionDetail.Build(_env, col);

        Assert.True(model.HasVanilla);
        Assert.Equal(new[] { "Vanilla", "ModA", "ModB" }, model.Columns);

        // 'a' is contested (mods disagree) and sorts first; 'b'/'c' untouched
        var a = model.Rows[0];
        Assert.Equal("a", a.Field);
        Assert.True(a.Contested);
        Assert.False(a.Cells[0].Changed);                 // vanilla baseline
        Assert.True(a.Cells[1].Changed);                  // ModA differs from vanilla
        Assert.True(a.Cells[2].Changed);
        Assert.DoesNotContain(model.Rows, r => r.Field is "b" or "c" && (r.Contested || r.AnyChanged));
    }

    [Fact]
    public void Build_ModAddedObject_NoVanillaColumn_PresentFieldsAreChanges()
    {
        var mA = Mod("ModA", "condowners", """[{"strName":"New","a":1}]""");
        var mB = Mod("ModB", "condowners", """[{"strName":"New","b":2}]""");
        var col = new Collision { Type = "condowners", ObjName = "New", Claimants = new() { mA, mB } };

        var model = CollisionDetail.Build(_env, col);

        Assert.False(model.HasVanilla);
        Assert.Equal(new[] { "ModA", "ModB" }, model.Columns);
        var a = model.Rows.Single(r => r.Field == "a");
        Assert.True(a.Cells[0].Present && a.Cells[0].Changed);   // ModA set it
        Assert.False(a.Cells[1].Present);                        // absent from ModB
        Assert.True(a.Contested);                                // present-vs-absent disagreement
    }

    [Fact]
    public void Build_ReportsClaimantWhoseObjectIsMissingOnDisk()
    {
        var mA = Mod("ModA", "conditions", """[{"strName":"X","a":1}]""");
        var ghost = new ModEntry { Raw = "Ghost", Kind = EntryKind.Local, Name = "Ghost", Dir = null, DisplayName = "Ghost" };
        var col = new Collision { Type = "conditions", ObjName = "X", Claimants = new() { mA, ghost } };

        var model = CollisionDetail.Build(_env, col);

        Assert.Contains("Ghost", model.MissingClaimants);
    }
}
