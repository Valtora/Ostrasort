using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace Ostrasort;

/// <summary>
/// Installs a mod from a .zip into the live game install - the missing bookend
/// to <see cref="ModRemoval"/>. Off-Workshop mods (FFU, GitHub releases, Nexus,
/// Discord-shared archives) arrive as zips the player would otherwise have to
/// unzip into Ostranauts_Data\Mods\ by hand; this detects what the archive
/// holds, extracts it to the right place (zip-slip safe, ReadOnly-safe
/// overwrite), and hands the freshly installed data mods to
/// <see cref="ModRegistration"/> for load-order registration.
///
/// Two component shapes are recognised:
///   - a <b>local data mod</b> (a folder holding mod_info.json and/or a data\ /
///     images\ subtree) -> extracted into the game's Mods folder, then
///     registered by folder name; and
///   - a <b>BepInEx bundle</b> (a BepInEx\ subtree, Thunderstore/FFU style) ->
///     merged into the game's BepInEx tree. Any plugins-dir data mod inside it
///     (mod_info.json + data\) is registered by absolute path; pure code
///     plugins are copied but not registered (BepInEx auto-loads them and their
///     load position is irrelevant).
///
/// GitHub-style wrapper folders (repo-main\...) are stripped automatically
/// because detection keys off the directory that DIRECTLY holds the mod
/// markers, and a multi-mod archive installs every mod it finds.
/// </summary>
public static class ModInstall
{
    public enum ComponentKind { LocalMod, BepInExBundle }

    /// <summary>One installable unit found inside the archive.</summary>
    /// <param name="Name">Display / install name (folder name for a local mod).</param>
    /// <param name="TargetDir">Absolute folder the component extracts into.</param>
    /// <param name="SourcePrefix">Archive-relative prefix stripped from each entry ("" = archive root).</param>
    /// <param name="SubtreeFilter">If set, only entries whose stripped path starts with this are extracted (BepInEx bundles).</param>
    /// <param name="HasData">True when it carries loadable data\ objects (so it is worth registering).</param>
    /// <param name="Exists">True when the target already exists on disk (a collision the caller resolves).</param>
    /// <param name="RegisterDirs">Absolute folders to check for registration after extraction.</param>
    public sealed record Component(
        string Name, string TargetDir, string SourcePrefix, string? SubtreeFilter,
        ComponentKind Kind, bool HasData, bool Exists, IReadOnlyList<string> RegisterDirs);

    public sealed record Plan(string ZipPath, IReadOnlyList<Component> Components, IReadOnlyList<string> Warnings)
    {
        public bool IsEmpty => Components.Count == 0;
    }

    public sealed record Result(
        IReadOnlyList<Component> Installed, IReadOnlyList<Component> Skipped, IReadOnlyList<string> Warnings);

    // The game loader tolerates comments (its own core data ships them); be at
    // least as lenient when reading a mod's mod_info.json for its strName.
    private static readonly JsonDocumentOptions Lenient = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    // ----------------------------------------------------------------- inspect ---

    /// <summary>
    /// Read the archive and work out what would be installed, writing nothing.
    /// Throws <see cref="InvalidDataException"/> if the file is not a readable
    /// zip or contains a path-traversal entry (fail closed - a malicious or
    /// broken archive is never partially installed).
    /// </summary>
    public static Plan Inspect(GameEnv env, string zipPath)
    {
        if (!File.Exists(zipPath))
            throw new FileNotFoundException($"'{zipPath}' does not exist.", zipPath);

        ZipArchive zip;
        try { zip = ZipFile.OpenRead(zipPath); }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            throw new InvalidDataException($"'{Path.GetFileName(zipPath)}' is not a readable .zip archive ({e.Message}).", e);
        }

        using (zip)
        {
            // normalised file paths inside the archive (directories dropped)
            var files = zip.Entries
                .Where(e => e.FullName.Length > 0 && !e.FullName.EndsWith('/') && !e.FullName.EndsWith('\\'))
                .Select(e => Norm(e.FullName))
                .ToList();

            // fail closed on any path-traversal entry before planning anything
            foreach (var f in files)
                if (IsUnsafePath(f))
                    throw new InvalidDataException(
                        $"'{Path.GetFileName(zipPath)}' contains an unsafe path '{f}' (path traversal) - refusing to install.");

            var components = new List<Component>();
            var warnings = new List<string>();

            // 1) BepInEx bundle(s): everything under a "BepInEx/" segment, grouped
            //    by the archive prefix that precedes it (usually "" or "wrapper/").
            var bundlePrefixes = files
                .Select(BepInExPrefix)
                .Where(p => p is not null)
                .Select(p => p!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var prefix in bundlePrefixes)
                components.Add(BuildBundle(env, files, prefix, warnings));

            // 2) local data-mod roots. mod_info.json is authoritative when present
            //    (each one's folder is a mod); otherwise fall back to data\ folders.
            //    Anything under a BepInEx bundle prefix belongs to that bundle.
            var infoRoots = files
                .Where(f => LastSegment(f).Equals("mod_info.json", StringComparison.OrdinalIgnoreCase))
                .Select(ParentDir)
                .Where(r => !IsUnderAnyBundle(r, bundlePrefixes))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var roots = infoRoots;
            if (roots.Count == 0)
            {
                roots = files
                    .Select(DataRoot)
                    .Where(r => r is not null && !IsUnderAnyBundle(r!, bundlePrefixes))
                    .Select(r => r!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (roots.Count > 0)
                    warnings.Add("no mod_info.json found - installing by the folder/archive name; the mod will still load, " +
                                 "but add a mod_info.json so it shows properly on the in-game MODS screen.");
            }

            foreach (var root in roots)
                components.Add(BuildLocalMod(env, zip, files, root, zipPath, warnings));

            if (components.Count == 0)
                warnings.Add("no recognisable Ostranauts mod found in the archive (expected a mod_info.json, a data\\ folder, " +
                             "or a BepInEx\\ bundle).");
            if (components.Any(c => c.Kind == ComponentKind.BepInExBundle))
                warnings.Add("this archive installs into the game's BepInEx tree - only install BepInEx mods you trust, " +
                             "they run real code.");

            return new Plan(zipPath, components, warnings);
        }
    }

    // ----------------------------------------------------------------- execute ---

    /// <summary>
    /// Extract the chosen components. A component whose target already exists is
    /// skipped unless <paramref name="overwrite"/> is set, in which case a local
    /// mod folder is deleted and replaced (ReadOnly-safe) and a BepInEx bundle is
    /// merged over the existing files. Writes nothing to loading_order.json - that
    /// is <see cref="RegisterInstalled"/>'s job.
    /// </summary>
    public static Result Execute(
        GameEnv env, Plan plan, IReadOnlyCollection<Component>? chosen = null, bool overwrite = false)
    {
        chosen ??= plan.Components;
        var chosenSet = new HashSet<Component>(chosen);
        var installed = new List<Component>();
        var skipped = new List<Component>();
        var warnings = new List<string>();

        using var zip = ZipFile.OpenRead(plan.ZipPath);
        foreach (var c in plan.Components)
        {
            if (!chosenSet.Contains(c)) continue;
            if (c.Exists && !overwrite) { skipped.Add(c); continue; }

            if (c.Kind == ComponentKind.LocalMod && c.Exists && overwrite && Directory.Exists(c.TargetDir))
                FileSystemUtil.RobustDeleteDirectory(c.TargetDir);   // clean replace, not a merge

            ExtractComponent(zip, c);
            installed.Add(c);
        }

        return new Result(installed, skipped, warnings);
    }

    private static void ExtractComponent(ZipArchive zip, Component c)
    {
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.Length == 0 || entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;
            var full = Norm(entry.FullName);
            if (!full.StartsWith(c.SourcePrefix, StringComparison.OrdinalIgnoreCase)) continue;

            var rel = full[c.SourcePrefix.Length..];
            if (rel.Length == 0) continue;
            if (c.SubtreeFilter is not null && !rel.StartsWith(c.SubtreeFilter, StringComparison.OrdinalIgnoreCase)) continue;
            // a local mod's own BepInEx\ subtree belongs in the game's BepInEx tree
            // (handled by the sibling bundle component), not inside Mods\<Name>\
            if (c.Kind == ComponentKind.LocalMod && rel.StartsWith("BepInEx/", StringComparison.OrdinalIgnoreCase)) continue;

            var dest = SafeCombine(c.TargetDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, overwrite: true);
        }
    }

    // -------------------------------------------------------------- registration ---

    /// <summary>
    /// Register every freshly installed data mod into loading_order.json in its
    /// suggested slot, reusing <see cref="ModRegistration"/>. Rescans the install
    /// and only touches mods that landed under one of this install's target
    /// folders; a mod already registered (an overwrite/update of an existing one)
    /// is left untouched. Returns the aLoadOrder entries added. The caller is
    /// responsible for the game-closed / rival-lock gates (registration writes).
    /// </summary>
    public static List<string> RegisterInstalled(GameEnv env, Result result)
    {
        var registerDirs = result.Installed
            .SelectMany(c => c.RegisterDirs)
            .Select(d => Path.TrimEndingDirectorySeparator(Path.GetFullPath(d)))
            .ToList();
        if (registerDirs.Count == 0) return new();

        var registered = new List<string>();
        var state = Engine.Analyze(env);
        foreach (var m in state.Analysis.UnregisteredLocal.ToList())
        {
            if (m.Dir is null) continue;
            var dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(m.Dir));
            var underInstall = registerDirs.Any(r =>
                dir.Equals(r, StringComparison.OrdinalIgnoreCase) ||
                dir.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            if (!underInstall) continue;
            try
            {
                var r = ModRegistration.Register(env, m, state.Analysis);
                if (!r.AlreadyRegistered) registered.Add(r.Entry);
            }
            catch (InvalidOperationException) { /* not a registerable kind (core/patch) - skip */ }
        }
        return registered;
    }

    // --------------------------------------------------------------- detection ---

    private static Component BuildLocalMod(
        GameEnv env, ZipArchive zip, List<string> files, string root, string zipPath, List<string> warnings)
    {
        var prefix = root.Length == 0 ? "" : root + "/";
        var name = LocalModName(zip, root, prefix, zipPath);
        var target = Path.Combine(env.ModsDir, name);
        var hasData = files.Any(f => f.StartsWith(prefix + "data/", StringComparison.OrdinalIgnoreCase));
        if (!hasData && files.Any(f => f.StartsWith(prefix + "images/", StringComparison.OrdinalIgnoreCase)))
            warnings.Add($"'{name}' ships images but no data\\ - it will install but has nothing to load.");
        return new Component(name, target, prefix, SubtreeFilter: null, ComponentKind.LocalMod,
            hasData, Directory.Exists(target), new[] { target });
    }

    private static Component BuildBundle(GameEnv env, List<string> files, string prefix, List<string> warnings)
    {
        var bep = prefix + "BepInEx/";
        // first-level folders introduced under BepInEx\plugins and BepInEx\patchers
        var leafFolders = new List<string>();
        foreach (var sub in new[] { "plugins/", "patchers/" })
        {
            var head = bep + sub;
            foreach (var f in files.Where(f => f.StartsWith(head, StringComparison.OrdinalIgnoreCase)))
            {
                var rest = f[head.Length..];
                var slash = rest.IndexOf('/');
                if (slash > 0) leafFolders.Add(sub + rest[..slash]);   // e.g. "plugins/Author-Mod"
            }
        }
        leafFolders = leafFolders.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var pluginFolders = leafFolders.Where(l => l.StartsWith("plugins/", StringComparison.OrdinalIgnoreCase)).ToList();
        var name = pluginFolders.Count > 0
            ? string.Join(", ", pluginFolders.Select(p => p["plugins/".Length..]))
            : leafFolders.Count > 0 ? string.Join(", ", leafFolders) : "BepInEx bundle";

        var hasData = files.Any(f =>
            f.StartsWith(bep + "plugins/", StringComparison.OrdinalIgnoreCase) &&
            f.Contains("/data/", StringComparison.OrdinalIgnoreCase));

        // register only plugins-dir data mods (they need an absolute-path entry);
        // patchers and loose DLLs auto-load and are never registered
        var registerDirs = pluginFolders
            .Select(p => Path.Combine(env.BepInExDir, "plugins", p["plugins/".Length..]))
            .ToArray();

        var exists = registerDirs.Any(Directory.Exists) ||
                     leafFolders.Any(l => Directory.Exists(Path.Combine(env.BepInExDir, l.Replace('/', Path.DirectorySeparatorChar))));

        return new Component(name, env.GameRoot, prefix, SubtreeFilter: "BepInEx/", ComponentKind.BepInExBundle,
            hasData, exists, registerDirs);
    }

    /// <summary>
    /// Installed folder name for a local mod: the mod's own strName (cleanest,
    /// and what shows on the MODS screen) when mod_info.json declares one, else
    /// the archive folder name, else the zip's base name. Sanitised to a legal
    /// folder name.
    /// </summary>
    private static string LocalModName(ZipArchive zip, string root, string prefix, string zipPath)
    {
        if (ReadStrName(zip, prefix + "mod_info.json") is { } strName && Sanitize(strName) is { Length: > 0 } clean)
            return clean;
        if (root.Length > 0 && Sanitize(LastSegment(root)) is { Length: > 0 } folder)
            return folder;
        return Sanitize(Path.GetFileNameWithoutExtension(zipPath)) is { Length: > 0 } zn ? zn : "InstalledMod";
    }

    private static string? ReadStrName(ZipArchive zip, string entryPath)
    {
        var entry = zip.GetEntry(entryPath) ?? zip.Entries.FirstOrDefault(e =>
            Norm(e.FullName).Equals(entryPath, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return null;
        try
        {
            using var s = entry.Open();
            using var reader = new StreamReader(s);
            using var doc = JsonDocument.Parse(reader.ReadToEnd(), Lenient);
            var el = doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0
                ? doc.RootElement[0] : doc.RootElement;
            if (el.ValueKind == JsonValueKind.Object &&
                el.TryGetProperty("strName", out var n) && n.ValueKind == JsonValueKind.String)
                return n.GetString();
        }
        catch { /* unreadable mod_info.json - fall back to the folder/zip name */ }
        return null;
    }

    // ------------------------------------------------------------------- helpers ---

    /// <summary>Normalise an archive path to forward slashes with no leading "./".</summary>
    private static string Norm(string p)
    {
        p = p.Replace('\\', '/');
        while (p.StartsWith("./", StringComparison.Ordinal)) p = p[2..];
        return p;
    }

    private static bool IsUnsafePath(string norm) =>
        norm.StartsWith('/') || norm.Contains(':') ||
        norm.Split('/').Any(seg => seg == "..");

    private static string LastSegment(string norm)
    {
        var i = norm.LastIndexOf('/');
        return i < 0 ? norm : norm[(i + 1)..];
    }

    private static string ParentDir(string norm)
    {
        var i = norm.LastIndexOf('/');
        return i < 0 ? "" : norm[..i];
    }

    /// <summary>The mod root of a "<root>/data/..." (or "data/...") file, or null.</summary>
    private static string? DataRoot(string norm)
    {
        if (norm.StartsWith("data/", StringComparison.OrdinalIgnoreCase)) return "";
        var idx = norm.IndexOf("/data/", StringComparison.OrdinalIgnoreCase);
        return idx < 0 ? null : norm[..idx];
    }

    /// <summary>The archive prefix before a "BepInEx/" segment, or null when the file is not part of a bundle.</summary>
    private static string? BepInExPrefix(string norm)
    {
        if (norm.StartsWith("BepInEx/", StringComparison.OrdinalIgnoreCase)) return "";
        var idx = norm.IndexOf("/BepInEx/", StringComparison.OrdinalIgnoreCase);
        return idx < 0 ? null : norm[..(idx + 1)];   // include the trailing slash
    }

    // Only a mod_info.json/data root that physically lives INSIDE a "BepInEx/"
    // segment belongs to the bundle. A sibling data-mod root that merely shares
    // the bundle's prefix (a combined data+code mod: mod_info.json + data\ +
    // BepInEx\ in one folder) is still its own local mod - its BepInEx\ subtree
    // is split out to the game tree at extraction time.
    private static bool IsUnderAnyBundle(string root, List<string> bundlePrefixes) =>
        bundlePrefixes.Any(p => (root + "/").StartsWith(p + "BepInEx/", StringComparison.OrdinalIgnoreCase));

    private static string? Sanitize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var cleaned = new string(raw.Where(ch => !Path.GetInvalidFileNameChars().Contains(ch)).ToArray())
            .Trim().Trim('.').Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    /// <summary>Combine a target root with an archive-relative path, refusing anything that escapes the root.</summary>
    private static string SafeCombine(string root, string rel)
    {
        var rootFull = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(rootFull, rel.Replace('/', Path.DirectorySeparatorChar)));
        var withSep = rootFull.EndsWith(Path.DirectorySeparatorChar) ? rootFull : rootFull + Path.DirectorySeparatorChar;
        if (!full.StartsWith(withSep, StringComparison.OrdinalIgnoreCase) &&
            !full.Equals(rootFull, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"archive entry '{rel}' escapes the install folder (path traversal) - refusing to install.");
        return full;
    }
}
