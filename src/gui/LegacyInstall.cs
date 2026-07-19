using System.IO;
using System.Runtime.InteropServices;

namespace Ostrasort.Gui;

/// <summary>
/// Best-effort removal of the pre-0.23 self-install. Up to 0.22 Ostrasort copied
/// itself to %LOCALAPPDATA%\Programs\Ostrasort and made Ostrasort.lnk shortcuts
/// aimed there. Velopack now owns install and shortcuts (into
/// %LOCALAPPDATA%\Ostrasort), so once the managed copy is running that old folder
/// is a dead duplicate that could still launch stale code. This deletes it, plus
/// any Desktop / Start Menu shortcut still pointing into it - never Velopack's
/// own, which targets the new install. It runs once (sentinel-gated) and only
/// from the managed install, and swallows every failure.
/// </summary>
public static class LegacyInstall
{
    private static string OldInstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ostrasort");

    /// <summary>True when the running exe lives inside the Velopack install root (%LOCALAPPDATA%\Ostrasort).</summary>
    private static bool RunningAsManagedInstall()
    {
        var cur = Environment.ProcessPath;
        if (string.IsNullOrEmpty(cur)) return false;
        try
        {
            var root = Path.GetFullPath(AppPaths.LegacyDataDir).TrimEnd('\\') + "\\";
            return Path.GetFullPath(cur).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static void Cleanup()
    {
        try
        {
            if (!RunningAsManagedInstall()) return;   // only the installed copy tidies up
            var sentinel = Path.Combine(AppPaths.DataDir, ".legacy-install-cleaned");
            if (File.Exists(sentinel)) return;
            Directory.CreateDirectory(AppPaths.DataDir);

            RemoveOldShortcuts();
            if (Directory.Exists(OldInstallDir))
            {
                try
                {
                    Directory.Delete(OldInstallDir, recursive: true);
                    OpLog.Add($"Removed the old self-install at {OldInstallDir}.");
                }
                catch { /* locked / in use - leave it, the sentinel still stops a retry loop */ }
            }
            File.WriteAllText(sentinel, $"{DateTime.UtcNow:o}\n");
        }
        catch { /* best effort */ }
    }

    private static void RemoveOldShortcuts()
    {
        foreach (var lnk in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Ostrasort.lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Ostrasort.lnk"),
        })
        {
            try
            {
                if (File.Exists(lnk) && TargetsOldInstall(lnk))
                {
                    File.Delete(lnk);
                    OpLog.Add($"Removed the old shortcut {lnk}.");
                }
            }
            catch { /* best effort */ }
        }
    }

    /// <summary>Reads a .lnk's target via Windows Script Host; true only if it points into the old install dir.</summary>
    private static bool TargetsOldInstall(string lnkPath)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell");
        if (type is null) return false;
        dynamic? shell = Activator.CreateInstance(type);
        if (shell is null) return false;
        try
        {
            dynamic sc = shell.CreateShortcut(lnkPath);
            string target = sc.TargetPath ?? "";
            return target.StartsWith(OldInstallDir, StringComparison.OrdinalIgnoreCase);
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }
}
