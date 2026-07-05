using System.IO;
using System.Text.Json;

namespace Ostrasort;

public enum EntryKind
{
    Core,
    Workshop,   // absolute path under the Steam workshop content folder
    Local,      // folder name under the game's Mods folder
    PluginDir,  // absolute path under BepInEx\plugins - how FFU/Thunderstore data mods are installed
}

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
    public string? PublishedId { get; set; }         // strWorkshopID from mod_info.json (published local mods)
    public bool Registered { get; set; } = true;

    /// <summary>Workshop item id: the content folder for subscriptions, strWorkshopID for published local mods.</summary>
    public string? WorkshopId => Kind == EntryKind.Workshop ? Name : PublishedId;

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
    public HashSet<string> ImagePaths { get; } = new(StringComparer.OrdinalIgnoreCase);   // relative under images\
    public HashSet<string> PluginDlls { get; } = new(StringComparer.OrdinalIgnoreCase);   // basenames under BepInEx\plugins
    public List<string> JsonErrors { get; } = new();

    // FFU (filled by the scanner + FfuAnalysis.Classify)
    public AutoloadMeta? Meta { get; set; }                  // Autoload.Meta.toml, when the mod ships one
    public bool IsFfu { get; set; }                          // belongs to the FFU block (loads after all non-FFU mods)
    public FfuLoadGroup FfuGroup { get; set; } = FfuLoadGroup.AfterFFU;   // effective tier when IsFfu
    public bool UsesElasticApi { get; set; }                 // FFU-only data features detected
    public List<string> FfuSignals { get; } = new();         // human-readable evidence for IsFfu
    public List<(string Type, string Id)> RemoveIds { get; } = new();     // mod_info.json "removeIds"
    public bool HasChangesMap { get; set; }                  // mod_info.json "changesMap"
    public HashSet<(string Type, string Name)> FfuArrayEditClaims { get; } = new();  // claims using --ADD--/--DEL--/... commands
    public bool IsFfuPatch { get; set; }                     // FFU convention: apply once, right after its target
    public ModEntry? FfuPatchTarget { get; set; }

    public string Label =>
        Kind switch
        {
            EntryKind.Core => "core",
            EntryKind.Workshop => $"{DisplayName ?? "?"} [{Name}]",
            EntryKind.PluginDir => $"{DisplayName ?? Name} ({Name}, plugins)",
            _ => $"{DisplayName ?? Name} ({Name}, local)",
        };

    public static ModEntry Parse(string raw, GameEnv env)
    {
        if (raw == "core")
            return new ModEntry { Raw = raw, Kind = EntryKind.Core, Name = "core", Dir = env.CoreDataDir };

        if (raw.Length > 2 && raw[1] == ':')   // absolute path: Workshop subscription or a BepInEx\plugins data mod
        {
            var underBepInEx = raw.StartsWith(env.BepInExDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                            || raw.StartsWith(env.BepInExDir + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            return new ModEntry
            {
                Raw = raw,
                Kind = underBepInEx ? EntryKind.PluginDir : EntryKind.Workshop,
                Name = Path.GetFileName(raw.TrimEnd('\\', '/')),
                Dir = Directory.Exists(raw) ? raw : null,
            };
        }

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
        foreach (var d in EnumerateDataObjects(env.CoreDataDir, wantLoot: false, _ => CoreProblemFiles++))
        {
            CoreIndex.Add((d.Type, d.Name));
            types.Add(d.Type);
        }
        CoreTypes = types.Count;
    }

    public void Scan(ModEntry mod)
    {
        if (mod.Kind == EntryKind.Core || mod.Dir is null) return;

        mod.HasPatchers = HasDlls(Path.Combine(mod.Dir, "BepInEx", "patchers"));
        mod.HasPlugins = HasDlls(Path.Combine(mod.Dir, "BepInEx", "plugins"));
        ReadModInfo(mod);
        mod.Meta = AutoloadMeta.Read(mod.Dir);

        var imagesDir = Path.Combine(mod.Dir, "images");
        if (Directory.Exists(imagesDir))
            foreach (var f in Directory.EnumerateFiles(imagesDir, "*.*", SearchOption.AllDirectories))
                mod.ImagePaths.Add(Path.GetRelativePath(imagesDir, f));

        var pluginsDir = Path.Combine(mod.Dir, "BepInEx", "plugins");
        if (Directory.Exists(pluginsDir))
            foreach (var f in Directory.EnumerateFiles(pluginsDir, "*.dll", SearchOption.AllDirectories))
                mod.PluginDlls.Add(Path.GetFileName(f));

        var dataDir = Path.Combine(mod.Dir, "data");
        if (Directory.Exists(dataDir))
        {
            bool anyReference = false, anyCommands = false;
            foreach (var d in EnumerateDataObjects(dataDir, wantLoot: true, mod.JsonErrors.Add))
            {
                mod.Claims[(d.Type, d.Name)] = d.Loots;
                mod.DataObjects++;
                if (CoreIndex.Contains((d.Type, d.Name))) mod.CoreOverrides++;
                anyReference |= d.HasReference;
                if (d.ArrayCommands)
                {
                    anyCommands = true;
                    mod.FfuArrayEditClaims.Add((d.Type, d.Name));
                }
            }
            if (anyReference) mod.FfuSignals.Add("strReference clone entries in its data");
            if (anyCommands) mod.FfuSignals.Add("--ADD--/--DEL--/--MOD--/--INS-- precision array commands");
            mod.UsesElasticApi |= anyReference || anyCommands;
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
            if (root.TryGetProperty("strWorkshopID", out var w) && w.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(w.GetString()))
                mod.PublishedId = w.GetString();

            // FFU-only mod_info extensions: entity removal + save-migration maps
            if (root.TryGetProperty("removeIds", out var rem) && rem.ValueKind == JsonValueKind.Object)
            {
                foreach (var typeProp in rem.EnumerateObject())
                    if (typeProp.Value.ValueKind == JsonValueKind.Array)
                        foreach (var id in typeProp.Value.EnumerateArray())
                            if (id.ValueKind == JsonValueKind.String)
                                mod.RemoveIds.Add((typeProp.Name, id.GetString()!));
                if (mod.RemoveIds.Count > 0)
                {
                    mod.UsesElasticApi = true;
                    mod.FfuSignals.Add($"removeIds in mod_info.json ({mod.RemoveIds.Count} entr(y/ies))");
                }
            }
            if (root.TryGetProperty("changesMap", out var cm) && cm.ValueKind == JsonValueKind.Object)
            {
                mod.HasChangesMap = true;
                mod.UsesElasticApi = true;
                mod.FfuSignals.Add("changesMap in mod_info.json");
            }
        }
        catch (JsonException e)
        {
            mod.JsonErrors.Add($"mod_info.json: {e.Message}");
        }
    }

    /// <summary>One data object found in a mod: its claim plus the FFU elastic-API markers it carries.</summary>
    internal readonly record struct DataObject(
        string Type, string Name, string[]? Loots, bool HasReference, bool ArrayCommands);

    /// <summary>FFU precision array commands: "--ADD--", "--INS--4", "5|--MOD--|..." sub-array rows, etc.</summary>
    private static readonly System.Text.RegularExpressions.Regex FfuCommand =
        new(@"(^|\|)--(ADD|DEL|MOD|INS)--", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Walks data\&lt;type&gt;\**\*.json and yields every object's (type, strName).
    /// Loot objects also carry their aLoots array for subset/superset analysis;
    /// an aLoots that uses FFU array commands is an EDIT, not a replacement, so
    /// its Loots come back null (non-comparable) with ArrayCommands set.
    /// Files that only parse leniently (trailing commas etc.) are reported via
    /// <paramref name="onError"/> - the game's own loader would ERROR on them.
    /// </summary>
    private static IEnumerable<DataObject> EnumerateDataObjects(
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

                        var hasReference = obj.TryGetProperty("strReference", out var refEl)
                                           && refEl.ValueKind == JsonValueKind.String;
                        var commands = HasFfuArrayCommands(obj);

                        string[]? loots = null;
                        if (wantLoot && type == "loot" &&
                            obj.TryGetProperty("aLoots", out var lootsEl) && lootsEl.ValueKind == JsonValueKind.Array)
                        {
                            var entries = lootsEl.EnumerateArray()
                                .Where(x => x.ValueKind == JsonValueKind.String)
                                .Select(x => x.GetString()!)
                                .ToArray();
                            // command-driven aLoots merge at load (FFU) - not a whole-pool replacement
                            if (!entries.Any(e => FfuCommand.IsMatch(e))) loots = entries;
                        }
                        yield return new DataObject(type, nameEl.GetString()!, loots, hasReference, commands);
                    }
                }
            }
        }
    }

    /// <summary>Any top-level array field containing an FFU command entry (incl. sub-array "N|--CMD--|..." rows).</summary>
    private static bool HasFfuArrayCommands(JsonElement obj)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Array) continue;
            foreach (var el in prop.Value.EnumerateArray())
                if (el.ValueKind == JsonValueKind.String && FfuCommand.IsMatch(el.GetString()!))
                    return true;
        }
        return false;
    }

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path);
}
