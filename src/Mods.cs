using System.Text.Json;

namespace Ostrasort;

public enum EntryKind { Core, Workshop, Local }

public enum ModClass
{
    Core,           // the game's own data
    Infrastructure, // ships BepInEx\patchers - the mod-loader class, pinned right after core
    Code,           // plugins/DLLs only, no data objects - position irrelevant
    Shell,          // metadata only (mod_info + empty data) - position irrelevant
    DataAdditive,   // adds new objects only - safe anywhere after core
    DataOverride,   // replaces core objects - after core (always true), watch for collisions
}

/// <summary>One aLoadOrder entry (or a discovered-but-unregistered mod folder).</summary>
public sealed class ModEntry
{
    public required string Raw { get; init; }        // exact aLoadOrder string ("" if unregistered)
    public required EntryKind Kind { get; init; }
    public required string Name { get; init; }       // folder name (local) / workshop item id
    public string? Dir { get; init; }                // resolved mod folder; null = dead entry
    public bool EditMarker { get; init; }            // "<Name>|edit"
    public string? DisplayName { get; set; }         // strName from mod_info.json
    public string? GameVersion { get; set; }         // strGameVersion from mod_info.json
    public bool Registered { get; set; } = true;

    private bool? _isPatch;
    /// <summary>True for the folder Ostrasort itself generates (identified by its marker file).</summary>
    public bool IsPatch => _isPatch ??= Dir is not null && File.Exists(Path.Combine(Dir, Patcher.MarkerFile));

    // filled by the scanner
    public ModClass Class { get; set; } = ModClass.Shell;
    public bool HasPatchers { get; set; }
    public bool HasPlugins { get; set; }
    public int DataObjects { get; set; }
    public int CoreOverrides { get; set; }
    public Dictionary<(string Type, string Name), string[]?> Claims { get; } = new();
    public List<string> JsonErrors { get; } = new();

    public string Label =>
        Kind switch
        {
            EntryKind.Core => "core",
            EntryKind.Workshop => $"{DisplayName ?? "?"} [{Name}]",
            _ => $"{DisplayName ?? Name} ({Name}, local)",
        };

    public static ModEntry Parse(string raw, GameEnv env)
    {
        if (raw == "core")
            return new ModEntry { Raw = raw, Kind = EntryKind.Core, Name = "core", Dir = env.CoreDataDir };

        if (raw.Length > 2 && raw[1] == ':')   // absolute path = subscribed Workshop item
            return new ModEntry
            {
                Raw = raw,
                Kind = EntryKind.Workshop,
                Name = Path.GetFileName(raw.TrimEnd('\\', '/')),
                Dir = Directory.Exists(raw) ? raw : null,
            };

        var edit = raw.EndsWith("|edit", StringComparison.Ordinal);
        var name = edit ? raw[..^5] : raw;
        var dir = Path.Combine(env.ModsDir, name);
        return new ModEntry
        {
            Raw = raw,
            Kind = EntryKind.Local,
            Name = name,
            Dir = Directory.Exists(dir) ? dir : null,
            EditMarker = edit,
        };
    }
}

public sealed class Scanner(GameEnv env)
{
    private static readonly JsonDocumentOptions Lenient = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    public HashSet<(string Type, string Name)> CoreIndex { get; } = new();
    public int CoreTypes { get; private set; }
    public int CoreProblemFiles { get; private set; }

    public void IndexCore()
    {
        var types = new HashSet<string>();
        // core ships a handful of files with non-standard JSON the game's own
        // (lenient) parser accepts; count them so the index gap is visible
        foreach (var (type, name, _) in EnumerateDataObjects(env.CoreDataDir, wantLoot: false, _ => CoreProblemFiles++))
        {
            CoreIndex.Add((type, name));
            types.Add(type);
        }
        CoreTypes = types.Count;
    }

    public void Scan(ModEntry mod)
    {
        if (mod.Kind == EntryKind.Core || mod.Dir is null) return;

        mod.HasPatchers = HasDlls(Path.Combine(mod.Dir, "BepInEx", "patchers"));
        mod.HasPlugins = HasDlls(Path.Combine(mod.Dir, "BepInEx", "plugins"));
        ReadModInfo(mod);

        var dataDir = Path.Combine(mod.Dir, "data");
        if (Directory.Exists(dataDir))
        {
            foreach (var (type, name, loots) in EnumerateDataObjects(dataDir, wantLoot: true, mod.JsonErrors.Add))
            {
                mod.Claims[(type, name)] = loots;
                mod.DataObjects++;
                if (CoreIndex.Contains((type, name))) mod.CoreOverrides++;
            }
        }

        mod.Class =
            mod.HasPatchers ? ModClass.Infrastructure :
            mod.DataObjects == 0 && mod.HasPlugins ? ModClass.Code :
            mod.DataObjects == 0 ? ModClass.Shell :
            mod.CoreOverrides > 0 ? ModClass.DataOverride :
            ModClass.DataAdditive;
    }

    private static bool HasDlls(string dir) =>
        Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.dll", SearchOption.AllDirectories).Any();

    private void ReadModInfo(ModEntry mod)
    {
        var path = Path.Combine(mod.Dir!, "mod_info.json");
        if (!File.Exists(path)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path), Lenient);
            var root = doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0
                ? doc.RootElement[0]
                : doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            if (root.TryGetProperty("strName", out var n) && n.ValueKind == JsonValueKind.String)
                mod.DisplayName = n.GetString();
            if (root.TryGetProperty("strGameVersion", out var v) && v.ValueKind == JsonValueKind.String)
                mod.GameVersion = v.GetString();
        }
        catch (JsonException e)
        {
            mod.JsonErrors.Add($"mod_info.json: {e.Message}");
        }
    }

    /// <summary>
    /// Walks data\&lt;type&gt;\**\*.json and yields every object's (type, strName).
    /// Loot objects also carry their aLoots array for subset/superset analysis.
    /// Files that only parse leniently (trailing commas etc.) are reported via
    /// <paramref name="onError"/> - the game's own loader would ERROR on them.
    /// </summary>
    private static IEnumerable<(string Type, string Name, string[]? Loots)> EnumerateDataObjects(
        string dataDir, bool wantLoot, Action<string> onError)
    {
        if (!Directory.Exists(dataDir)) yield break;
        foreach (var typeDir in Directory.EnumerateDirectories(dataDir))
        {
            var type = Path.GetFileName(typeDir);
            if (type.Equals("schemas", StringComparison.OrdinalIgnoreCase)) continue;

            foreach (var file in Directory.EnumerateFiles(typeDir, "*.json", SearchOption.AllDirectories))
            {
                JsonDocument doc;
                var text = File.ReadAllText(file);
                try
                {
                    doc = JsonDocument.Parse(text);   // strict, like the game
                }
                catch (JsonException)
                {
                    try
                    {
                        doc = JsonDocument.Parse(text, Lenient);
                        onError($"{Relative(dataDir, file)}: parses only leniently (trailing comma/comment?) - the game load would ERROR");
                    }
                    catch (JsonException e)
                    {
                        onError($"{Relative(dataDir, file)}: invalid JSON - {e.Message}");
                        continue;
                    }
                }

                using (doc)
                {
                    var objects = doc.RootElement.ValueKind == JsonValueKind.Array
                        ? doc.RootElement.EnumerateArray().ToArray()
                        : [doc.RootElement];
                    foreach (var obj in objects)
                    {
                        if (obj.ValueKind != JsonValueKind.Object) continue;
                        if (!obj.TryGetProperty("strName", out var nameEl) || nameEl.ValueKind != JsonValueKind.String)
                            continue;

                        string[]? loots = null;
                        if (wantLoot && type == "loot" &&
                            obj.TryGetProperty("aLoots", out var lootsEl) && lootsEl.ValueKind == JsonValueKind.Array)
                        {
                            loots = lootsEl.EnumerateArray()
                                .Where(x => x.ValueKind == JsonValueKind.String)
                                .Select(x => x.GetString()!)
                                .ToArray();
                        }
                        yield return (type, nameEl.GetString()!, loots);
                    }
                }
            }
        }
    }

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path);
}
