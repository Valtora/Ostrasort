using System.Text.Json.Nodes;
using Ostrasort;

namespace Ostrasort.Tests;

/// <summary>Small factories so tests can build model objects without a game install.</summary>
internal static class TestData
{
    public static ModEntry Mod(string name) =>
        new() { Raw = name, Kind = EntryKind.Local, Name = name, Dir = name, DisplayName = name };

    public static ModEntry Workshop(string id, string display) =>
        new() { Raw = $@"C:\ws\{id}", Kind = EntryKind.Workshop, Name = id, Dir = $@"C:\ws\{id}", DisplayName = display };

    public static Collision Coll(string type, string obj, params ModEntry[] claimants) =>
        new() { Type = type, ObjName = obj, Claimants = claimants.ToList() };

    public static JsonNode Obj(string json) => JsonNode.Parse(json)!;

    public static List<(ModEntry, JsonNode)> Versions(params (ModEntry Mod, string Json)[] vs) =>
        vs.Select(v => (v.Mod, Obj(v.Json))).ToList();
}
