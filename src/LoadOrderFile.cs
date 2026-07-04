using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ostrasort;

/// <summary>
/// Guarded access to loading_order.json. The file MUST stay a top-level JSON
/// array (the game deserializes JsonModList[]; anything else makes it fall
/// back to a no-mods load and regenerate the file, silently dropping every
/// local mod). Every write: .bak of the original text first, serialized text
/// must start with '[', and a strict re-parse round-trip check.
/// </summary>
public sealed class LoadOrderFile
{
    public required string Path { get; init; }
    public required string RawText { get; init; }
    public required JsonNode Root { get; init; }
    public required List<string> Order { get; init; }

    public static LoadOrderFile Read(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"loading_order.json not found at '{path}'. Launch the game once so it generates one.", path);

        var raw = File.ReadAllText(path);
        if (!raw.TrimStart().StartsWith('['))
            throw new InvalidDataException(
                "loading_order.json is NOT a top-level JSON array - the game may already have " +
                "regenerated it after a corruption. Refusing to touch it; inspect it manually.");

        var root = JsonNode.Parse(raw) ?? throw new InvalidDataException("loading_order.json parsed to null.");
        if (root is not JsonArray arr || arr.Count == 0 || arr[0]?["aLoadOrder"] is not JsonArray orderArr)
            throw new InvalidDataException("loading_order.json has no [0].aLoadOrder array.");

        var order = orderArr.Select(n => n?.GetValue<string>()
            ?? throw new InvalidDataException("null entry inside aLoadOrder")).ToList();

        return new LoadOrderFile { Path = path, RawText = raw, Root = root, Order = order };
    }

    public void Write(IReadOnlyList<string> newOrder)
    {
        Root[0]!["aLoadOrder"] = new JsonArray(newOrder.Select(e => (JsonNode)e).ToArray());
        var json = Root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        if (!json.TrimStart().StartsWith('['))
            throw new InvalidOperationException(
                "Refusing to write loading_order.json: serialized result is not a top-level JSON array.");

        using (var doc = JsonDocument.Parse(json))   // strict round-trip check
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Round-trip check failed: root is not an array.");
            var count = doc.RootElement[0].GetProperty("aLoadOrder").GetArrayLength();
            if (count != newOrder.Count)
                throw new InvalidOperationException(
                    $"Round-trip check failed: {count} entries serialized, expected {newOrder.Count}.");
        }

        File.WriteAllText(Path + ".bak", RawText);   // original text, before this write
        File.WriteAllText(Path, json);
    }
}
