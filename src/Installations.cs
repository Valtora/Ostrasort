using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ostrasort;

/// <summary>
/// One saved install: a friendly name plus optional game-root and mods-folder
/// overrides. A null/blank override means "auto-detect" for that slot (game root
/// via Steam, mods folder from the game's own strPathMods or the default). This
/// lets a single Ostrasort manage several installs - e.g. a plain Workshop setup
/// and an FFU setup on different disks - and switch between them.
/// </summary>
public sealed class Installation
{
    public string Name { get; set; } = "";
    public string? GameRoot { get; set; }   // null/blank => auto-detect via Steam
    public string? ModsDir { get; set; }     // null/blank => derive from the game (strPathMods / default)

    /// <summary>Normalised override (blank => null, trimmed). Computed, not persisted.</summary>
    [JsonIgnore] public string? Game => string.IsNullOrWhiteSpace(GameRoot) ? null : GameRoot.Trim();
    [JsonIgnore] public string? Mods => string.IsNullOrWhiteSpace(ModsDir) ? null : ModsDir.Trim();
}

/// <summary>
/// The saved installs plus which one is active, persisted globally in
/// %LOCALAPPDATA%\Ostrasort\installations.json. Deliberately separate from
/// load-order <see cref="Profile"/>s: profiles are per-install order snapshots
/// keyed by the mods-folder path, so each install keeps its own set for free.
/// Active == null means the implicit "auto-detect" install (no overrides).
/// </summary>
public sealed class InstallationStore
{
    public int Version { get; set; } = 1;
    public string? Active { get; set; }
    public List<Installation> Items { get; set; } = new();

    private static string PathFor() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Ostrasort", "installations.json");

    public static InstallationStore Load()
    {
        try
        {
            var p = PathFor();
            if (File.Exists(p))
                return JsonSerializer.Deserialize<InstallationStore>(File.ReadAllText(p)) ?? new InstallationStore();
        }
        catch { /* corrupt store is not worth an error - start empty (auto-detect) */ }
        return new InstallationStore();
    }

    public void Save()
    {
        try
        {
            var p = PathFor();
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best effort */ }
    }

    /// <summary>The saved install with this name (case-insensitive), or null (incl. for the auto-detect slot).</summary>
    public Installation? Find(string? name) => string.IsNullOrWhiteSpace(name)
        ? null
        : Items.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Add or replace an install by name (case-insensitive match on the existing name).</summary>
    public void Upsert(Installation install)
    {
        var i = Items.FindIndex(x => string.Equals(x.Name, install.Name, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) Items[i] = install; else Items.Add(install);
    }

    public void Remove(string name)
    {
        Items.RemoveAll(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (string.Equals(Active, name, StringComparison.OrdinalIgnoreCase)) Active = null;
    }
}
