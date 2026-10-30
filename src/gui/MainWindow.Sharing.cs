using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace Ostrasort.Gui;

/// <summary>Sharing a profile with other players, and importing theirs or a Workshop collection.</summary>
public partial class MainWindow
{
    private void ProfileShare_Click(object sender, RoutedEventArgs e)
    {
        if (_state is null || ListProfiles.SelectedItem is not ProfileRow row) return;
        var list = ModListSharing.FromProfile(row.Profile, _env, _state.Analysis);
        var copy = new MenuItem { Header = "Copy share code" };
        copy.Click += (_, _) =>
        {
            Clipboard.SetText(ModListSharing.ToCode(list));
            OpLog.Add($"Copied a share code for profile '{row.Name}'.");
            RunStatus.Text = $"Share code for '{row.Name}' copied. Paste it to another player; they use Import… on their Profiles tab.  ";
            RunStatus.Foreground = Good;
        };
        var file = new MenuItem { Header = "Save as file…" };
        file.Click += (_, _) =>
        {
            var dlg = new SaveFileDialog
            {
                FileName = string.Concat(row.Name.Split(Path.GetInvalidFileNameChars())) + ModListSharing.FileExtension,
                Filter = $"Ostrasort mod list|*{ModListSharing.FileExtension}",
            };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                File.WriteAllText(dlg.FileName, ModListSharing.ToJson(list));
                OpLog.Add($"Exported profile '{row.Name}' to {dlg.FileName}.");
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Ostrasort", MessageBoxButton.OK, MessageBoxImage.Error); }
        };
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Items = { copy, file } };
        menu.IsOpen = true;
    }

    private void ProfileImport_Click(object sender, RoutedEventArgs e)
    {
        if (_state is null) return;
        var dlg = new ImportListDialog(_env, _state.Analysis, Http) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.SavedName is not { } name) return;
        OpLog.Add($"Imported a mod list as profile '{name}'.");
        RenderProfiles();
        SelectProfile(name);
        RunStatus.Text = $"Profile '{name}' saved. Press Switch… to load it.  ";
        RunStatus.Foreground = Good;
    }
}