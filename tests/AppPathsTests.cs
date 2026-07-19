using System.IO;
using Ostrasort;
using Xunit;

namespace Ostrasort.Tests;

/// <summary>
/// The one-time move of pre-0.23 user data from %LOCALAPPDATA%\Ostrasort to the
/// roaming data root. Verified against temp dirs (never the real profile): the
/// known data items move, unknown siblings (the Velopack install lives in the
/// same legacy folder) are left alone, a newer destination is never clobbered,
/// and the sentinel makes it a no-op forever after.
/// </summary>
public class AppPathsTests : IDisposable
{
    private readonly string _legacy = Path.Combine(Path.GetTempPath(), "OstraLegacy_" + Guid.NewGuid().ToString("N"));
    private readonly string _data = Path.Combine(Path.GetTempPath(), "OstraData_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        foreach (var d in new[] { _legacy, _data })
            try { if (Directory.Exists(d)) Directory.Delete(d, recursive: true); } catch { /* best effort */ }
    }

    private static void WriteFile(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void Migrate_MovesKnownFilesAndFolders_LeavesUnknownAlone()
    {
        WriteFile(Path.Combine(_legacy, "settings.json"), "{\"Theme\":\"dark\"}");
        WriteFile(Path.Combine(_legacy, "installations.json"), "{}");
        WriteFile(Path.Combine(_legacy, "ignored.json"), "[]");
        WriteFile(Path.Combine(_legacy, "ostrasort.log"), "log");
        WriteFile(Path.Combine(_legacy, "coreindex-abc123.json"), "{}");
        WriteFile(Path.Combine(_legacy, "profiles", "deadbeef", "p.json"), "{}");
        WriteFile(Path.Combine(_legacy, "backups", "deadbeef", "b.json"), "{}");
        // Velopack's own files share the legacy folder - they must be untouched.
        WriteFile(Path.Combine(_legacy, "current", "Ostrasort.dll"), "binary");
        WriteFile(Path.Combine(_legacy, "Update.exe"), "binary");

        var moved = AppPaths.Migrate(_legacy, _data);

        Assert.Equal(7, moved);   // 4 known files + 1 coreindex + profiles + backups
        Assert.Equal("{\"Theme\":\"dark\"}", File.ReadAllText(Path.Combine(_data, "settings.json")));
        Assert.True(File.Exists(Path.Combine(_data, "coreindex-abc123.json")));
        Assert.True(File.Exists(Path.Combine(_data, "profiles", "deadbeef", "p.json")));
        Assert.True(File.Exists(Path.Combine(_data, "backups", "deadbeef", "b.json")));

        // moved items are gone from legacy; the Velopack files stay
        Assert.False(File.Exists(Path.Combine(_legacy, "settings.json")));
        Assert.False(Directory.Exists(Path.Combine(_legacy, "profiles")));
        Assert.True(File.Exists(Path.Combine(_legacy, "current", "Ostrasort.dll")));
        Assert.True(File.Exists(Path.Combine(_legacy, "Update.exe")));

        Assert.True(File.Exists(Path.Combine(_data, ".migrated")));
    }

    [Fact]
    public void Migrate_MergesFolders_WhenDestinationAlreadyHasSome()
    {
        // Regression: a destination folder that already exists (here backups\keyA,
        // written by an earlier run) must not strand the rest of the source. keyB
        // has to come across, and the pre-existing keyA must be left as-is.
        WriteFile(Path.Combine(_legacy, "backups", "keyA", "old.json"), "legacyA");
        WriteFile(Path.Combine(_legacy, "backups", "keyB", "b.json"), "legacyB");
        WriteFile(Path.Combine(_legacy, "profiles", "keyP", "p.json"), "legacyP");
        WriteFile(Path.Combine(_data, "backups", "keyA", "new.json"), "roamingA");   // dest already has keyA

        AppPaths.Migrate(_legacy, _data);

        Assert.True(File.Exists(Path.Combine(_data, "backups", "keyB", "b.json")));   // stranded key rescued
        Assert.True(File.Exists(Path.Combine(_data, "profiles", "keyP", "p.json")));  // profile rescued
        Assert.Equal("roamingA", File.ReadAllText(Path.Combine(_data, "backups", "keyA", "new.json")));  // dest kept
        Assert.False(File.Exists(Path.Combine(_data, "backups", "keyA", "old.json")));                   // not overwritten/merged into
    }

    [Fact]
    public void Migrate_IsNoOpOnceSentinelExists()
    {
        Directory.CreateDirectory(_data);
        File.WriteAllText(Path.Combine(_data, ".migrated"), "done");
        WriteFile(Path.Combine(_legacy, "settings.json"), "legacy");

        var moved = AppPaths.Migrate(_legacy, _data);

        Assert.Equal(0, moved);
        Assert.True(File.Exists(Path.Combine(_legacy, "settings.json")));   // left where it was
        Assert.False(File.Exists(Path.Combine(_data, "settings.json")));
    }

    [Fact]
    public void Migrate_DoesNotClobberNewerDestination()
    {
        WriteFile(Path.Combine(_legacy, "settings.json"), "old");
        WriteFile(Path.Combine(_data, "settings.json"), "new");

        AppPaths.Migrate(_legacy, _data);

        Assert.Equal("new", File.ReadAllText(Path.Combine(_data, "settings.json")));   // roaming copy wins
        Assert.True(File.Exists(Path.Combine(_legacy, "settings.json")));              // legacy left intact
    }

    [Fact]
    public void Migrate_WithNoLegacyDir_JustWritesSentinel()
    {
        var moved = AppPaths.Migrate(_legacy, _data);   // _legacy never created

        Assert.Equal(0, moved);
        Assert.True(File.Exists(Path.Combine(_data, ".migrated")));
    }
}
