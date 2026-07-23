using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ostrasort;

/// <summary>
/// Per-install manual pins for a mod's load priority. Detection is data-driven
/// (see <see cref="CategoryAnalysis"/>), but a player sometimes needs the final
/// word: a mod that must load late even though it ships no character-generation
/// signal, a mod they want to keep out of the late block, or one they want to
/// yield early. This opt-in list records a mod's forced <see cref="LoadTier"/>
/// so the suggestion respects it on every rescan instead of re-proposing a move.
/// Purely a sorting preference: no game files change, it is per-install scoped,
/// and clearing a pin restores auto-detection. Mirrors <see cref="FfuOverrideList"/>
/// and <see cref="IgnoreList"/> for identity, persisted to %APPDATA%\Ostrasort.
/// </summary>
public sealed class CategoryOverrideList
{
    private readonly string _path;
    private readonly Dictionary<string, LoadTier> _tiers = new(StringComparer.OrdinalIgnoreCase);

    public static string DefaultPath => AppPaths.File("category-overrides.json");

    public CategoryOverrideList(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject obj)
                foreach (var (key, val) in obj)
                    if (val?.GetValueKind() == JsonValueKind.String &&
                        CategoryAnalysis.ParseTier(val.GetValue<string>()) is { } tier)
                        _tiers[key] = tier;
        }
        catch { /* unreadable list = no pins */ }
    }

    public static CategoryOverrideList LoadDefault() => new(DefaultPath);

    /// <summary>Stable per-install identity for a mod - identical to <see cref="FfuOverrideList.KeyFor"/>.</summary>
    public static string KeyFor(GameEnv env, ModEntry m) => FfuOverrideList.KeyFor(env, m);

    public bool TryGet(string key, out LoadTier tier) => _tiers.TryGetValue(key, out tier);
    public bool TryGet(GameEnv env, ModEntry m, out LoadTier tier) => _tiers.TryGetValue(KeyFor(env, m), out tier);

    /// <summary>Pins a mod to a tier. Persists immediately.</summary>
    public void Set(string key, LoadTier tier)
    {
        _tiers[key] = tier;
        Save();
    }

    /// <summary>Clears a mod's pin, restoring auto-detection. Persists immediately.</summary>
    public void Clear(string key)
    {
        if (_tiers.Remove(key)) Save();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var obj = new JsonObject();
            foreach (var (key, tier) in _tiers.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                obj[key] = tier switch { LoadTier.Late => "late", LoadTier.Early => "early", _ => "normal" };
            File.WriteAllText(_path, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* a failed save only forgets the preference */ }
    }
}
