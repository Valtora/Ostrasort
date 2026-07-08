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
    public bool Disabled { get; init; }              // "<entry>|disabled" - the game keeps the entry but skips it at load
    public string? DisplayName { get; set; }         // strName from mod_info.json
    public string? GameVersion { get; set; }         // strGameVersion from mod_info.json
    public string? ModVersion { get; set; }          // strModVersion from mod_info.json (the mod's own version)
    public string? PublishedId { get; set; }         // strWorkshopID from mod_info.json (published local mods)
    public bool Registered { get; set; } = true;
    public bool Ignored { get; set; }                // user chose to leave it unregistered (see IgnoreList)

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
    /// <summary>Condition names this mod defines via conditions_simple (they land in the conditions namespace).</summary>
    public HashSet<string> SimpleConditionNames { get; } = new(StringComparer.Ordinal);
    /// <summary>Data files of this mod skipped by loading_order.json's aIgnorePatterns (rel path, pattern).</summary>
    public List<(string File, string Pattern)> IgnoredFiles { get; } = new();

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

        // marker parsing mirrors the game's JsonModList.ParseLoadingOrderEntry:
        // "<path>|edit" arms the Upload button, "<path>|disabled" keeps the entry
        // but skips it at load (the in-game MODS screen toggle), and a 3+-part
        // entry is treated as disabled
        var parts = raw.Split('|');
        var path = parts[0];
        var edit = parts.Length == 2 && parts[1] == "edit";
        var disabled = (parts.Length == 2 && parts[1] == "disabled") || parts.Length >= 3;

        if (path.Length > 2 && path[1] == ':')   // absolute path: Workshop subscription or a BepInEx\plugins data mod
        {
            var underBepInEx = path.StartsWith(env.BepInExDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                            || path.StartsWith(env.BepInExDir + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            return new ModEntry
            {
                Raw = raw,
                Kind = underBepInEx ? EntryKind.PluginDir : EntryKind.Workshop,
                Name = Path.GetFileName(path.TrimEnd('\\', '/')),
                Dir = Directory.Exists(path) ? path : null,
                Disabled = disabled,
            };
        }

        var dir = Path.Combine(env.ModsDir, path);
        return new ModEntry
        {
            Raw = raw,
            Kind = EntryKind.Local,
            Name = path,
            Dir = Directory.Exists(dir) ? dir : null,
            EditMarker = edit,
            Disabled = disabled,
        };
    }

    /// <summary>
    /// This entry's raw aLoadOrder string with the |disabled marker toggled -
    /// the same edit the in-game MODS screen makes. Re-enabled local mods get
    /// their |edit marker back (the game strips it when disabling).
    /// </summary>
    public string RawToggledDisabled(bool disabled)
    {
        var path = (Raw.Length > 0 ? Raw : Name).Split('|')[0];
        if (disabled) return path + "|disabled";
        return Kind == EntryKind.Local ? path + "|edit" : path;
    }

    /// <summary>
    /// A note when the mod's declared strGameVersion does not match the
    /// installed game, worded by direction; null when they match (or the mod
    /// declares none).
    /// </summary>
    public string? GameVersionNote(string? installed)
    {
        if (GameVersion is not { Length: > 0 } gv || installed is not { Length: > 0 } iv || gv == iv) return null;
        if (Version.TryParse(gv, out var vm) && Version.TryParse(iv, out var vi))
            return vm < vi
                ? $"gameVersion {gv} predates game {iv} (mod may be outdated)"
                : $"gameVersion {gv} is newer than game {iv}";
        return $"gameVersion {gv} differs from game {iv}";
    }
}

public sealed class Scanner(GameEnv env, IReadOnlyList<string>? ignorePatterns = null, bool useCoreCache = true)
{
    private static readonly JsonDocumentOptions Lenient = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    // What the game's own loader actually tolerates: // and /* */ comments, but
    // NOT trailing commas. The game ships comments in its own core data (e.g.
    // tokens/verbs.json, conditions_simple/conditions_simple.json), so a file
    // that parses once comments are skipped is game-legal and must not be flagged.
    private static readonly JsonDocumentOptions CommentsOnly = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private readonly IReadOnlyList<string> _ignore = ignorePatterns ?? [];

    /// <summary>
    /// Data folders whose files are flat-packed "JsonSimple" containers: a
    /// single object whose <c>strName</c> is just a container label and whose
    /// <c>aValues</c> is a fixed-width record array the game explodes into
    /// individual records AFTER every mod loads (DataHandler.ParseConditionsSimple
    /// and friends), merging them into a target namespace key-by-key. The game
    /// NEVER whole-object-replaces one of these, so two mods each shipping their
    /// own container do not lose anything unless they define the same record.
    /// The value is a short, human description of where those records land -
    /// used in the collision notes. Treating a container as a mergeable whole
    /// object (a union of the flat <c>aValues</c>) corrupts the packing and
    /// crashes the game on load, so these are always report-only, never patched.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> SimpleContainerTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["conditions_simple"] = "conditions",
            ["strings"] = "game strings",
            ["names_first"] = "first names",
            ["names_full"] = "full names",
            ["names_last"] = "last names",
            ["names_robots"] = "robot names",
            ["names_ship"] = "ship names",
            ["names_ship_adjectives"] = "ship-name adjectives",
            ["names_ship_nouns"] = "ship-name nouns",
            ["crewskins"] = "crew skins",
            ["manpages"] = "manual pages",
            ["traitscores"] = "trait scores",
        };

    public HashSet<(string Type, string Name)> CoreIndex { get; } = new();
    public int CoreTypes { get; private set; }
    public int CoreProblemFiles { get; private set; }
    /// <summary>Core data files skipped by loading_order.json's aIgnorePatterns (rel path, pattern).</summary>
    public List<(string File, string Pattern)> IgnoredCoreFiles { get; } = new();

    /// <summary>
    /// The game's aIgnorePatterns test (DataHandler.LoadModJsons): the file's
    /// forward-slash path simply CONTAINS the (sanitized) pattern.
    /// </summary>
    public static bool IsIgnored(string fullPath, IReadOnlyList<string>? patterns, out string? pattern)
    {
        pattern = null;
        if (patterns is not { Count: > 0 }) return false;
        var sanitized = fullPath.Replace('\\', '/').Replace("//", "/");
        pattern = patterns.FirstOrDefault(p => sanitized.Contains(p, StringComparison.Ordinal));
        return pattern is not null;
    }

    public void IndexCore()
    {
        // parsing every core JSON is the expensive part of a scan, and core only
        // changes when the game updates - cache the raw (pattern-independent)
        // index and re-apply aIgnorePatterns on every load
        var snap = useCoreCache ? CoreIndexCache.TryLoad(env.CoreDataDir) : null;
        if (snap is null)
        {
            var entries = new List<CoreIndexCache.Entry>();
            var problems = 0;
            // count only files the game's own loader would reject (trailing
            // commas); // and /* */ comments are game-legal and parse silently
            foreach (var d in EnumerateDataObjects(env.CoreDataDir, wantLoot: false, _ => problems++))
                entries.Add(new CoreIndexCache.Entry(d.Type, d.Name, d.RelPath));
            snap = new CoreIndexCache.Snapshot(entries, problems);
            if (useCoreCache) CoreIndexCache.Save(env.CoreDataDir, snap);
        }
        CoreProblemFiles = snap.ProblemFiles;

        var types = new HashSet<string>();
        var ignoredFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in snap.Entries)
        {
            if (IsIgnored(Path.Combine(env.CoreDataDir, e.RelPath), _ignore, out var pat))
            {
                if (ignoredFiles.Add(e.RelPath)) IgnoredCoreFiles.Add((e.RelPath, pat!));
                continue;
            }
            CoreIndex.Add((e.Type, e.Name));
            types.Add(e.Type);
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
            var ignoredFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in EnumerateDataObjects(dataDir, wantLoot: true, mod.JsonErrors.Add,
                         _ignore, (file, pat) => { if (ignoredFiles.Add(file)) mod.IgnoredFiles.Add((file, pat)); }))
            {
                mod.Claims[(d.Type, d.Name)] = d.Loots;
                mod.DataObjects++;
                if (d.FromSimple) mod.SimpleConditionNames.Add(d.Name);
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

    /// <summary>true / "true" / "1" / a non-zero number all read as true (mod_info bools are sometimes strings).</summary>
    private static bool JsonTrue(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.String => bool.TryParse(e.GetString(), out var b) ? b : e.GetString() == "1",
        JsonValueKind.Number => e.TryGetDouble(out var d) && d != 0,
        _ => false,
    };

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
            if (root.TryGetProperty("strModVersion", out var mv) && mv.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(mv.GetString()))
                mod.ModVersion = mv.GetString();
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

            // Ostrasort-specific opt-in hint. A pure FFU field-merge mod (partial
            // objects that overwrite only a few fields by strName) carries no
            // auto-detectable marker - it is content-identical to a normal whole-
            // object override - so Ostrasort would otherwise move it up out of the
            // FFU block. bFFU lets the author declare "sort me as FFU-dependent".
            // It is a SORTING HINT ONLY: FFU's field-merge is automatic in-game and
            // neither FFU nor the game reads this key. The standard Autoload.Meta.toml
            // LoadGroup is still detected as well (this is the mod_info route).
            if (root.TryGetProperty("bFFU", out var ffu) && JsonTrue(ffu))
            {
                mod.UsesElasticApi = true;
                mod.FfuSignals.Add("bFFU hint in mod_info.json");
            }
        }
        catch (JsonException e)
        {
            mod.JsonErrors.Add($"mod_info.json: {e.Message}");
        }
    }

    /// <summary>One data object found in a mod: its claim plus the FFU elastic-API markers it carries.</summary>
    internal readonly record struct DataObject(
        string Type, string Name, string[]? Loots, bool HasReference, bool ArrayCommands,
        string RelPath, bool FromSimple = false);

    /// <summary>FFU precision array commands: "--ADD--", "--INS--4", "5|--MOD--|..." sub-array rows, etc.</summary>
    private static readonly System.Text.RegularExpressions.Regex FfuCommand =
        new(@"(^|\|)--(ADD|DEL|MOD|INS)--", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Walks data\&lt;type&gt;\**\*.json and yields every object's (type, strName).
    /// Loot objects also carry their aLoots array for subset/superset analysis;
    /// an aLoots that uses FFU array commands is an EDIT, not a replacement, so
    /// its Loots come back null (non-comparable) with ArrayCommands set.
    /// A conditions_simple container additionally yields one entry per condition
    /// it defines, under type "conditions" with FromSimple set - the game parses
    /// them into the same dictionary as conditions\ (after every mod loads).
    /// Files matching an aIgnorePattern are skipped exactly as the game skips
    /// them, reported via <paramref name="onIgnored"/>. Comments (// and /* */)
    /// are accepted silently - the game's own loader allows them (core ships them,
    /// e.g. tokens/verbs.json). Only a trailing comma (or otherwise invalid JSON)
    /// is reported via <paramref name="onError"/> - that the game would ERROR on.
    /// </summary>
    internal static IEnumerable<DataObject> EnumerateDataObjects(
        string dataDir, bool wantLoot, Action<string> onError,
        IReadOnlyList<string>? ignorePatterns = null, Action<string, string>? onIgnored = null)
    {
        if (!Directory.Exists(dataDir)) yield break;
        foreach (var typeDir in Directory.EnumerateDirectories(dataDir))
        {
            var type = Path.GetFileName(typeDir);
            if (type.Equals("schemas", StringComparison.OrdinalIgnoreCase)) continue;

            foreach (var file in Directory.EnumerateFiles(typeDir, "*.json", SearchOption.AllDirectories))
            {
                var relPath = Relative(dataDir, file);
                if (IsIgnored(file, ignorePatterns, out var pattern))
                {
                    onIgnored?.Invoke(relPath, pattern!);
                    continue;
                }

                JsonDocument doc;
                var text = File.ReadAllText(file);
                try
                {
                    doc = JsonDocument.Parse(text);   // strict
                }
                catch (JsonException)
                {
                    // Strict rejects both comments and trailing commas; the game
                    // rejects only trailing commas. Retry allowing comments but not
                    // trailing commas: if THAT parses, the file is game-legal (just
                    // has comments) and gets no warning. Only if it still needs
                    // trailing-comma tolerance would the game's loader ERROR.
                    try
                    {
                        doc = JsonDocument.Parse(text, CommentsOnly);   // comments only - game-legal
                    }
                    catch (JsonException)
                    {
                        try
                        {
                            doc = JsonDocument.Parse(text, Lenient);
                            onError($"{relPath}: has a trailing comma - the game load would ERROR");
                        }
                        catch (JsonException e)
                        {
                            onError($"{relPath}: invalid JSON - {e.Message}");
                            continue;
                        }
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
                        yield return new DataObject(type, nameEl.GetString()!, loots, hasReference, commands, relPath);

                        // each 7-tuple in a conditions_simple container defines one
                        // condition in the CONDITIONS namespace (ParseConditionsSimple)
                        if (type.Equals("conditions_simple", StringComparison.OrdinalIgnoreCase) &&
                            obj.TryGetProperty("aValues", out var valsEl) && valsEl.ValueKind == JsonValueKind.Array)
                        {
                            var vals = valsEl.EnumerateArray()
                                .Where(x => x.ValueKind == JsonValueKind.String)
                                .Select(x => x.GetString()!)
                                .ToArray();
                            for (var i = 0; i + 6 < vals.Length; i += 7)
                                yield return new DataObject("conditions", vals[i], null, false, false, relPath, FromSimple: true);
                        }
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
