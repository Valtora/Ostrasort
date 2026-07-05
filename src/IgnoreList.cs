using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ostrasort;

/// <summary>
/// Mods the user has deliberately left unregistered. Without this every
/// unregistered local folder is a permanent warning plus a permanent "add"
/// suggestion - there was no way to keep a mod parked on disk. Keys embed the
/// install's Mods folder (or the plugins-dir path), so entries never leak
/// between installs or fixtures. Persisted to %LOCALAPPDATA%\Ostrasort.
/// </summary>
public sealed class IgnoreList
{
    private readonly string _path;
    private readonly HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase);

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Ostrasort", "ignored.json");

    public IgnoreList(string path)
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

    public static IgnoreList LoadDefault() => new(DefaultPath);

    /// <summary>Stable identity for an unregistered mod, scoped to its install.</summary>
    public static string KeyFor(GameEnv env, ModEntry m) =>
        m.Kind == EntryKind.PluginDir && m.Dir is not null ? m.Dir : $"{env.ModsDir}|{m.Name}";

    public bool Contains(string key) => _keys.Contains(key);

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
