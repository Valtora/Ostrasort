using System.Diagnostics;
using System.IO;
using System.Windows;

namespace Ostrasort.Gui;

/// <summary>
/// Self-adopting updater. The download stays manual (the update prompt opens the
/// GitHub release page), but when the user runs that freshly downloaded exe this
/// makes it supersede the installed copy: if it is a newer build than the one at
/// <see cref="SelfInstall.InstalledExePath"/>, it asks the running installed
/// instance to close, overwrites the installed binary with itself, refreshes the
/// shortcuts, and relaunches from the install location - so the user never has to
/// re-run the self-install by hand. Detection is by the exe's embedded version,
/// never its filename (a download may land as "Ostrasort-v0.21.0.exe" or
/// "Ostrasort (1).exe"). GUI-only, and it runs before the single-instance check
/// so the mutex doesn't just focus the old window instead of replacing it.
/// </summary>
public static class Updater
{
    /// <summary>A newer running exe that should replace the installed copy.</summary>
    public sealed record PendingUpdate(string RunningExe, string InstalledExe, string RunningVersion, string InstalledVersion);

    /// <summary>Canonical version parse shared with the launch update check: strip a leading v and any +build/-suffix.</summary>
    internal static Version Parse(string s) =>
        Version.TryParse((s ?? "").TrimStart('v', 'V').Split('+', '-')[0], out var v) ? v : new Version(0, 0);

    /// <summary>Pure, testable: is <paramref name="runningVer"/> strictly newer than <paramref name="installedVer"/>?</summary>
    public static bool ShouldAdopt(string runningVer, string installedVer) => Parse(runningVer) > Parse(installedVer);

    /// <summary>
    /// Returns a pending update when the running exe should adopt the install
    /// location. Null (do nothing) unless ALL hold: we know our own path and are
    /// not already the installed copy; an installed copy exists to update; we are
    /// not a developer build running from a bin\ output folder; and our version is
    /// strictly newer than the installed one.
    /// </summary>
    public static PendingUpdate? Detect()
    {
        var cur = SelfInstall.CurrentExePath;
        if (cur is not { Length: > 0 }) return null;                 // dotnet-run host etc. - can't self-replace
        if (SelfInstall.IsInstalled()) return null;                  // we ARE the installed copy - nothing to adopt
        if (IsDevBuildPath(cur)) return null;                        // bin\Debug|Release dev launch - don't nag
        var installedExe = SelfInstall.InstalledExePath;
        if (!File.Exists(installedExe)) return null;                 // no prior install (first-run offer handles this)

        var installedVer = ReadExeVersion(installedExe);
        if (installedVer is null) return null;
        if (!ShouldAdopt(Program.Version, installedVer)) return null;

        return new PendingUpdate(cur, installedExe, Program.Version, installedVer);
    }

    /// <summary>Shows the themed confirm and, if accepted, performs the swap + relaunch. True = handed off (caller should exit).</summary>
    public static bool PromptAndApply(PendingUpdate pu)
    {
        var accepted = UpdateDialog.ShowConfirm(
            "Update your installed Ostrasort?",
            $"You're running v{pu.RunningVersion}, and your installed copy is v{pu.InstalledVersion}.\n\n" +
            "Ostrasort will close the running copy, replace the installed one, refresh your shortcuts, and restart.",
            confirmLabel: "Update and restart",
            cancelLabel: "Just run this copy");
        if (!accepted) return false;
        return Apply(pu);
    }

    /// <summary>
    /// Closes the old installed instance (gracefully, force-killing only on
    /// timeout), overwrites the installed exe with the running one, refreshes any
    /// existing shortcuts, and relaunches the installed copy. Returns true when
    /// the relaunch happened (caller must exit this process); false on failure
    /// (caller falls through to running this downloaded copy in place).
    /// </summary>
    private static bool Apply(PendingUpdate pu)
    {
        try
        {
            OpLog.Add($"Update: adopting install location (running v{pu.RunningVersion} over installed v{pu.InstalledVersion}).");

            // Ask a running installed instance to close, then wait for it to let
            // go of its own exe. No-op if nothing is running there.
            SingleInstance.SignalShutdownExisting();
            if (!WaitForReplaceable(pu.InstalledExe, TimeSpan.FromSeconds(5)))
            {
                ForceKill(pu.InstalledExe);
                if (!WaitForReplaceable(pu.InstalledExe, TimeSpan.FromSeconds(3)))
                {
                    OpLog.Add("Update: installed exe stayed locked - aborting the automatic update.");
                    MessageBox.Show(
                        "The installed copy is still in use and couldn't be replaced.\n\n" +
                        "Close any running Ostrasort and run this download again, or just keep using it from here.",
                        "Ostrasort", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
            }

            CopyWithRetry(pu.RunningExe, pu.InstalledExe);
            foreach (var s in SelfInstall.RefreshShortcuts()) OpLog.Add($"Update: refreshed shortcut {s}.");

            Process.Start(new ProcessStartInfo(pu.InstalledExe) { UseShellExecute = true });
            OpLog.Add($"Update: replaced installed copy and relaunched v{pu.RunningVersion}.");
            return true;
        }
        catch (Exception e)
        {
            OpLog.Add($"Update: automatic update failed: {e.Message}");
            MessageBox.Show(
                "Ostrasort couldn't replace the installed copy automatically:\n\n" + e.Message +
                "\n\nYou can keep using this downloaded copy.",
                "Ostrasort", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    /// <summary>Reads a build's version from its embedded resource, without launching it. Null if unreadable.</summary>
    private static string? ReadExeVersion(string exePath)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exePath);
            return info.ProductVersion ?? info.FileVersion;
        }
        catch { return null; }
    }

    /// <summary>A dev launch (dotnet build/publish output) lives under a bin\ folder - never treat it as an update.</summary>
    private static bool IsDevBuildPath(string exePath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(exePath)) ?? "";
        return dir.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase) ||
               dir.EndsWith(@"\bin", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True once the file can be opened for exclusive write = no process is holding it (the running exe releases it on exit).</summary>
    private static bool WaitForReplaceable(string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        do
        {
            try
            {
                using var _ = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return true;
            }
            catch (IOException) { Thread.Sleep(150); }
            catch (UnauthorizedAccessException) { Thread.Sleep(150); }
        } while (DateTime.UtcNow < deadline);
        return false;
    }

    /// <summary>
    /// Last resort: kill a stuck installed GUI instance. Only targets processes
    /// whose image is the installed exe AND that own a main window - so a headless
    /// Ostrasort (e.g. an Ostraplan ship-mod registration tapping the installed
    /// exe, which has no window) is never killed mid-write.
    /// </summary>
    private static void ForceKill(string installedExe)
    {
        foreach (var p in Process.GetProcessesByName("Ostrasort"))
        {
            try
            {
                if (p.MainWindowHandle != IntPtr.Zero &&
                    string.Equals(p.MainModule?.FileName, installedExe, StringComparison.OrdinalIgnoreCase))
                {
                    p.Kill();
                    p.WaitForExit(3000);
                }
            }
            catch { /* not ours to kill / already gone / access denied - move on */ }
            finally { p.Dispose(); }
        }
    }

    /// <summary>Overwrite the installed exe, retrying briefly through transient locks (AV scans just after unlock).</summary>
    private static void CopyWithRetry(string source, string dest)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { File.Copy(source, dest, overwrite: true); return; }
            catch (IOException) when (attempt < 5) { Thread.Sleep(200); }
        }
    }
}
