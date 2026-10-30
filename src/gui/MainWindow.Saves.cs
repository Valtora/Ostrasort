using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Ostrasort.Gui;

/// <summary>
/// The Saves tab: zipped backups of the game's Saves folder, and the mod set
/// each save was made with (recorded when the game is started from Launch).
/// </summary>
public partial class MainWindow
{
    private Task? _saveBackup;
    private readonly SaveModSets.Store _saveSets = SaveModSets.Load();

    private sealed record SaveRow(string Name, string Detail, SaveModSets.SaveRecord? Record);
    private sealed record SaveBackupRow(string Name, string Detail, SaveBackups.Backup Backup);

    /// <summary>
    /// Runs on every scan, before rendering: closes a finished launch, links
    /// saves written during it, warns when the newest save's mods are gone, and
    /// takes the first-scan-after-a-game-update backup.
    /// </summary>
    private void TrackSaves(EngineState s)
    {
        try
        {
            var now = DateTime.UtcNow;
            var running = GameEnv.IsGameRunning();
            DateTime? logWrite = File.Exists(GameEnv.PlayerLogPath) ? File.GetLastWriteTimeUtc(GameEnv.PlayerLogPath) : null;
            var hadOpen = _saveSets.Launches.Any(l => l.EndedUtc is null);
            SaveModSets.CloseFinished(_saveSets, running, logWrite, now);
            var linked = SaveModSets.Link(_saveSets, SaveBackups.DefaultSavesDir, now);
            if (linked || (hadOpen && !running)) SaveModSets.Save(_saveSets);
            SaveModSets.Annotate(s.Analysis, _env, _saveSets, SaveBackups.DefaultSavesDir);
            if (SaveBackups.GameUpdatedSinceLastScan(_env)) BackupSaves("game update");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            OpLog.Add($"Could not read the Saves folder: {ex.Message}");
        }
    }

    /// <summary>
    /// Backs up the Saves folder in the background. Backups queue behind each
    /// other, and Launch waits for the last one, so the game never starts
    /// while a backup is still reading the saves.
    /// </summary>
    private async void BackupSaves(string reason, bool manual = false)
    {
        if (!manual && !_settings.BackupSaves) return;
        var previous = _saveBackup;
        var task = Task.Run(async () =>
        {
            if (previous is not null) try { await previous; } catch { /* reported by its own caller */ }
            return SaveBackups.Create(reason);
        });
        _saveBackup = task;
        try
        {
            var b = await task;
            if (b is not null)
            {
                OpLog.Add($"Backed up the Saves folder ({reason}): {Path.GetFileName(b.Path)}, {b.Bytes / (1024 * 1024)} MB.");
                if (manual) { RunStatus.Text = "Saves backed up.  "; RunStatus.Foreground = Good; }
            }
            else if (manual)
            {
                RunStatus.Text = "No save changed since the last backup, so there was nothing new to back up.  ";
                RunStatus.Foreground = Dim;
            }
        }
        catch (Exception ex)
        {
            OpLog.Add($"Save backup failed ({reason}): {ex.Message}");
            if (manual) MessageBox.Show(this, ex.Message, "Ostrasort", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        RenderSaves();
    }

    private async Task WaitForSaveBackupAsync()
    {
        if (_saveBackup is not { IsCompleted: false } pending) return;
        RunStatus.Text = "Finishing the save backup first…  ";
        RunStatus.Foreground = Dim;
        try { await pending; } catch { /* already logged */ }
    }

    /// <summary>
    /// After a launch from Ostrasort: waits for the game to start and exit, then
    /// closes the launch so the saves written in it link to its mod set.
    /// </summary>
    private async Task WatchGameAsync()
    {
        Process? game = null;
        for (var i = 0; i < 90 && game is null; i++)
        {
            var procs = Process.GetProcessesByName("Ostranauts");
            game = procs.FirstOrDefault();
            foreach (var p in procs.Skip(1)) p.Dispose();
            if (game is null) await Task.Delay(2000);
        }
        if (game is null) return;
        using (game) await game.WaitForExitAsync();
        SaveModSets.CloseFinished(_saveSets, gameRunning: false, null, DateTime.UtcNow);
        SaveModSets.Link(_saveSets, SaveBackups.DefaultSavesDir, DateTime.UtcNow);
        SaveModSets.Save(_saveSets);
        RenderSaves();
    }

    private void RecordLaunch()
    {
        if (_state is null) return;
        SaveModSets.RecordLaunch(_saveSets, _state.Analysis, _env, DateTime.UtcNow);
        SaveModSets.Save(_saveSets);
        _ = WatchGameAsync();
    }

    private void RenderSaves()
    {
        if (ListSaves is null) return;
        var savesDir = SaveBackups.DefaultSavesDir;
        var rows = new List<SaveRow>();
        if (Directory.Exists(savesDir))
        {
            var dirs = Directory.EnumerateDirectories(savesDir)
                .Select(d => (Name: Path.GetFileName(d), Written: Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories)
                    .Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max()))
                .OrderByDescending(d => d.Written);
            foreach (var (name, written) in dirs)
            {
                _saveSets.Saves.TryGetValue(name, out var rec);
                var mine = rec is not null && string.Equals(rec.LoadingOrderPath, _env.LoadingOrderPath, StringComparison.OrdinalIgnoreCase);
                var detail = $"saved {written.ToLocalTime():yyyy-MM-dd HH:mm}   ·   ";
                if (rec is null) detail += "mod set not recorded (the game was not started from Ostrasort)";
                else if (!mine) detail += "made with another installation's mods";
                else
                {
                    var count = rec.Mods.Count(e => e.Raw.Split('|')[0] != "core" && !e.Raw.EndsWith("|disabled", StringComparison.OrdinalIgnoreCase));
                    detail += $"made with {count} mod(s)";
                    if (_state is not null && SaveModSets.MissingNow(rec, _state.Analysis) is { Count: > 0 } missing)
                        detail += $", {missing.Count} of them do not load now";
                }
                rows.Add(new SaveRow(name, detail, mine ? rec : null));
            }
        }
        ListSaves.ItemsSource = rows;

        ListSaveBackups.ItemsSource = SaveBackups.List()
            .Select(b => new SaveBackupRow($"{b.TakenUtc.ToLocalTime():yyyy-MM-dd HH:mm}",
                $"{(b.Reason.Length > 0 ? b.Reason : "backup")}   ·   {b.Bytes / (1024 * 1024)} MB", b))
            .ToList();
        UpdateSaveButtons();
    }

    private void UpdateSaveButtons()
    {
        if (BtnSaveRestore is null) return;
        BtnSaveRestore.IsEnabled = ListSaveBackups.SelectedItem is SaveBackupRow;
        BtnSaveProfile.IsEnabled = ListSaves.SelectedItem is SaveRow { Record: not null };
    }

    private void Saves_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSaveButtons();

    private void BackupSaves_Changed(object sender, RoutedEventArgs e)
    {
        if (ChkBackupSaves is null) return;
        _settings.BackupSaves = ChkBackupSaves.IsChecked == true;
        SaveSettings();
    }

    private void SaveBackupNow_Click(object sender, RoutedEventArgs e) => BackupSaves("manual", manual: true);

    private void SaveOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(SaveBackups.DefaultBackupDir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{SaveBackups.DefaultBackupDir}\"") { UseShellExecute = true });
    }

    private async void SaveRestore_Click(object sender, RoutedEventArgs e)
    {
        if (ListSaveBackups.SelectedItem is not SaveBackupRow row || !GateRunning()) return;
        if (MessageBox.Show(this,
                $"Replace your saves with the backup from {row.Name}?\n\n" +
                "Your current saves are backed up first, so this can be undone the same way.\n\n" +
                "If Steam Cloud syncs your Ostranauts saves, Steam may report a sync conflict on the next launch. " +
                "Keep the files on this computer to keep the restored saves.",
                "Restore saves", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await WaitForSaveBackupAsync();
        _busy = true;
        try
        {
            var before = await Task.Run(() => SaveBackups.Restore(row.Backup));
            OpLog.Add($"Restored the Saves folder from {Path.GetFileName(row.Backup.Path)}" +
                      (before is null ? "." : $" (the saves it replaced are in {Path.GetFileName(before.Path)})."));
            RunStatus.Text = "Saves restored.  ";
            RunStatus.Foreground = Good;
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Ostrasort", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { _busy = false; }
        RenderSaves();
    }

    private void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ListSaves.SelectedItem is not SaveRow { Record: { } rec } row) return;
        var name = PromptDialog.Ask(this, "Name the profile for this save's mod set:", $"Save: {row.Name}");
        if (name is null) return;
        if (ProfileStore.Exists(_env.LoadingOrderPath, name) &&
            MessageBox.Show(this, $"A profile named “{name}” already exists. Overwrite it?",
                "Ostrasort", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        try
        {
            var profile = new Profile
            {
                Name = name, SavedAt = rec.LaunchedUtc.ToLocalTime().ToString("o"), SavedGameVersion = rec.GameVersion,
                Entries = rec.Mods.ToList(),
            };
            ProfileStore.Save(_env.LoadingOrderPath, profile);
            OpLog.Add($"Saved profile '{name}' from the mod set of save '{row.Name}' ({profile.ModCount} mods).");
            RenderProfiles();
            SelectProfile(name);
            Tabs.SelectedItem = TabProfiles;
            RunStatus.Text = $"Profile '{name}' saved. Press Switch… to load that mod set.  ";
            RunStatus.Foreground = Good;
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Ostrasort", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}
