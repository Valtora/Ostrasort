using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ostrasort;

/// <summary>
/// "I have dealt with this, stop showing me the red banner." The game overwrites
/// Player.log at every launch, so the issues it reported stay in the file until
/// the next one: a player who has fixed the mod, or decided to live with the
/// error, had no way to clear the alarm short of launching the game again.
///
/// What is stored is the fingerprint of the dismissed issues
/// (<see cref="Analysis.LogFingerprint"/>), not a flag, so the alarm returns the
/// moment the logs say something different - which includes the same error
/// happening again, because a new launch rewrites the log and changes its stamp.
///
/// Keyed by install, so two installs never dismiss each other's problems.
/// Persisted to %APPDATA%\Ostrasort. This is a GUI banner preference only: the
/// console reports every log issue regardless.
/// </summary>
public sealed class LogDismissal
{
    private readonly string _path;
    private readonly Dictionary<string, string> _byInstall = new(StringComparer.OrdinalIgnoreCase);

    public static string DefaultPath => AppPaths.File("log-dismissed.json");

    public LogDismissal(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject obj)
                foreach (var (k, v) in obj)
                    if (v?.GetValueKind() == JsonValueKind.String)
                        _byInstall[k] = v.GetValue<string>();
        }
        catch { /* unreadable list = nothing dismissed */ }
    }

    public static LogDismissal LoadDefault() => new(DefaultPath);

    /// <summary>Stable identity for an install (its game folder).</summary>
    public static string KeyFor(GameEnv env) => env.GameRoot;

    /// <summary>True when this exact set of log issues has already been dismissed.</summary>
    public bool IsDismissed(GameEnv env, Analysis a) =>
        a.LogFingerprint is { Length: > 0 } fp
        && _byInstall.TryGetValue(KeyFor(env), out var seen)
        && string.Equals(seen, fp, StringComparison.Ordinal);

    /// <summary>Dismisses the current set. A no-op when the logs reported nothing.</summary>
    public void Dismiss(GameEnv env, Analysis a)
    {
        if (a.LogFingerprint is not { Length: > 0 } fp) return;
        _byInstall[KeyFor(env)] = fp;
        Save();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var obj = new JsonObject();
            foreach (var (k, v) in _byInstall.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                obj[k] = v;
            File.WriteAllText(_path, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* a failed save only forgets the dismissal */ }
    }
}
