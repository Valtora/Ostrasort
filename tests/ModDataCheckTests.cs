using System.IO;
using Ostrasort;
using Xunit;

namespace Ostrasort.Tests;

public class ModDataCheckTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "OstraData_" + Guid.NewGuid().ToString("N"));
    private readonly GameEnv _env;

    public ModDataCheckTests()
    {
        var schemas = Path.Combine(_root, "core", "schemas");
        Directory.CreateDirectory(schemas);
        File.WriteAllText(Path.Combine(schemas, "condowners-schema.json"), """
            { "type": "array", "items": { "type": "object", "additionalProperties": false,
              "required": ["strName"],
              "properties": { "strName": {"type":"string"}, "strItemDef": {"type":"string"}, "strLoot": {"type":["string","null"]},
                              "strReference": {"type":"string"}, "nCount": {"type":"integer"} } } }
            """);
        _env = new GameEnv { GameRoot = _root, DiscoveredVia = "test", CoreDataDir = Path.Combine(_root, "core"), ModsDir = Path.Combine(_root, "Mods") };
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string ModData(string condownersJson)
    {
        var data = Path.Combine(_root, "Mods", "M", "data");
        Directory.CreateDirectory(Path.Combine(data, "condowners"));
        File.WriteAllText(Path.Combine(data, "condowners", "c.json"), condownersJson);
        return data;
    }

    private List<string> Run(string json, IEnumerable<string>? vanillaShapes = null, bool partial = false)
    {
        var m = new ModEntry { Raw = "M|edit", Kind = EntryKind.Local, Name = "M", Dir = Path.Combine(_root, "Mods", "M") };
        var defined = new HashSet<(string, string)> { ("items", "ItmLamp"), ("loot", "LootLamp") };
        var baseline = new Lazy<Dictionary<string, HashSet<string>>>(() => new()
        {
            ["condowners"] = (vanillaShapes ?? Array.Empty<string>()).ToHashSet(),
        });
        ModDataCheck.Check(m, ModData(json), defined, new SchemaValidator(_env), baseline, partial);
        return m.DataProblems;
    }

    [Fact]
    public void DanglingReference_AndMisspeltField_AreFound()
    {
        var problems = Run("""
            [ { "strName": "CoLamp", "strItemDef": "ItmLampp", "strLoot": "LootLamp", "fMas": 1 } ]
            """);

        Assert.Contains(problems, p => p.Contains("strItemDef names items \"ItmLampp\""));
        Assert.Contains(problems, p => p.Contains("unknown property \"fMas\""));
        Assert.DoesNotContain(problems, p => p.Contains("LootLamp"));
        Assert.All(problems, p => Assert.StartsWith("condowners/c.json: CoLamp:", p));
    }

    [Fact]
    public void AnErrorShapeVanillaAlsoHas_IsNotTheModsFault()
    {
        var problems = Run("""[ { "strName": "CoLamp", "strItemDef": "ItmLamp", "aExtra": [1], "nCount": "two" } ]""",
            vanillaShapes: new[] { "(root): unknown property \"aExtra\" (not allowed by the schema)" });

        Assert.Equal(new[] { "condowners/c.json: CoLamp: nCount: expected integer, got string" }, problems);
    }

    [Fact]
    public void PartialEdits_AndClones_SkipTheCompletenessChecks()
    {
        Assert.Empty(Run("""[ { "strItemDef": "ItmLamp" } ]""", partial: true).Where(p => p.Contains("missing required")));
        Assert.Contains(Run("""[ { "strItemDef": "ItmLamp" } ]"""), p => p.Contains("missing required property \"strName\""));
        Assert.Empty(Run("""[ { "strName": "CoLamp2", "strReference": "CoLamp", "fWhatever": 1 } ]"""));
    }

    [Theory]
    [InlineData("aConds[3]: expected string, got number", "aConds[]: expected string, got number")]
    [InlineData("strType: value \"X\" is not one of the allowed values [\"A\"]", "strType: not an allowed value")]
    [InlineData("fMass: -1 is below the minimum 0", "fMass: range")]
    public void Signature_DropsPositionsAndValues(string error, string shape) =>
        Assert.Equal(shape, ModDataCheck.Signature(error));
}
