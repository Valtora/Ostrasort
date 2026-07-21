using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ostrasort;

/// <summary>
/// Mods the user has manually declared FFU-dependent (load after Minor Fixes
/// Plus). Some FFU content mods - especially ones subscribed through the Steam
/// Workshop - carry no auto-detectable FFU marker: no <c>Autoload.Meta.toml</c>,
/// no elastic-API data, no <c>bFFU</c> hint (and you cannot edit a subscribed
/// Workshop mod's files to add one). Ostrasort would leave them in the non-FFU
/// block, so they load BEFORE Minor Fixes Plus - the exact thing FFU forbids.
/// This opt-in list lets the user tag such a mod so <see cref="FfuAnalysis.Classify"/>
/// seeds it into the FFU block. Purely a sorting preference: no game files are
/// touched, it is per-install scoped, and it can only ever move a mod later, so
/// it cannot mis-sort a plain Workshop mod on its own. Persisted to
/// %APPDATA%\Ostrasort.
/// </summary>
public sealed class FfuOverrideList
{
    private readonly string _path;
    private readonly HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase);

    public static string DefaultPath => AppPaths.File("ffu-overrides.json");

    public FfuOverrideList(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonArray arr)
                foreach (var n in arr)
                    if (n?.GetValueKind() == JsonValueKind.String)
                        _keys.Add(n.GetValue<string>());
        }
        catch { /* unreadable list = empty list */ }
    }

    public static FfuOverrideList LoadDefault() => new(DefaultPath);

    /// <summary>
    /// Stable identity for a mod, scoped to its install. A plugins-dir mod is
    /// keyed by its absolute folder; every other mod (local folder or Workshop
    /// item) by the Mods folder plus its <see cref="ModEntry.Name"/> - a folder
    /// name for local mods, the Workshop id for subscribed ones. Mirrors
    /// <see cref="IgnoreList.KeyFor"/> so the two preference files agree on identity.
    /// </summary>
    public static string KeyFor(GameEnv env, ModEntry m) =>
        m.Kind == EntryKind.PluginDir && m.Dir is not null ? m.Dir : $"{env.ModsDir}|{m.Name}";

    public bool Contains(string key) => _keys.Contains(key);
    public bool Contains(GameEnv env, ModEntry m) => _keys.Contains(KeyFor(env, m));

    public void Add(string key)
    {
        if (_keys.Add(key)) Save();
    }

    public void Remove(string key)
    {
        if (_keys.Remove(key)) Save();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var arr = new JsonArray(_keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                .Select(k => (JsonNode)k).ToArray());
            File.WriteAllText(_path, arr.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* a failed save only forgets the preference */ }
    }
}
