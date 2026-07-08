using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace Ostrasort.Gui;

/// <summary>
/// Add / edit / remove saved installations - a friendly name plus optional game
/// and mods folders, each overridable and possibly on different disks. Edits a
/// working copy; on Save it writes the list back into the passed store (the
/// caller persists it). Doubles as the first-run / recovery picker GuiHost shows
/// when Locate fails, in which case an error banner explains why.
/// </summary>
public partial class InstallationsDialog : Window
{
    private readonly InstallationStore _store;
    private readonly ObservableCollection<Installation> _items;
    private bool _loading;

    public InstallationsDialog(InstallationStore store, string? errorMessage)
    {
        _store = store;
        _items = new ObservableCollection<Installation>(
            store.Items.Select(i => new Installation { Name = i.Name, GameRoot = i.GameRoot, ModsDir = i.ModsDir }));
        InitializeComponent();
        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            TxtError.Text = "Could not open an install: " + errorMessage +
                            "  Add or fix an installation below, then Save & Close.";
            ErrorBanner.Visibility = Visibility.Visible;
        }
        ListInstalls.ItemsSource = _items;
        if (_items.Count > 0) ListInstalls.SelectedIndex = 0;
        UpdateEditorEnabled();
    }

    private Installation? Selected => ListInstalls.SelectedItem as Installation;

    private void List_Changed(object sender, SelectionChangedEventArgs e)
    {
        _loading = true;
        var s = Selected;
        TxtName.Text = s?.Name ?? "";
        TxtGame.Text = s?.GameRoot ?? "";
        TxtMods.Text = s?.ModsDir ?? "";
        _loading = false;
        UpdateEditorEnabled();
    }

    private void Field_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loading || Selected is not { } s) return;
        s.Name = TxtName.Text;
        s.GameRoot = TxtGame.Text;
        s.ModsDir = TxtMods.Text;
        ListInstalls.Items.Refresh();   // reflect the edited name in the list
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var inst = new Installation { Name = UniqueName("New install") };
        _items.Add(inst);
        ListInstalls.SelectedItem = inst;
        TxtName.Focus();
        TxtName.SelectAll();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } s) _items.Remove(s);
        if (_items.Count > 0 && ListInstalls.SelectedIndex < 0) ListInstalls.SelectedIndex = 0;
        UpdateEditorEnabled();
    }

    private void BrowseGame_Click(object sender, RoutedEventArgs e) => Browse(TxtGame);
    private void BrowseMods_Click(object sender, RoutedEventArgs e) => Browse(TxtMods);

    private void Browse(TextBox target)
    {
        var dlg = new OpenFolderDialog { Title = "Select a folder" };
        if (!string.IsNullOrWhiteSpace(target.Text))
            try { dlg.InitialDirectory = target.Text; } catch { /* a bad initial dir just opens the default */ }
        if (dlg.ShowDialog(this) == true) target.Text = dlg.FolderName;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // drop blank rows, trim, and require distinct non-empty names
        var cleaned = _items
            .Where(i => !string.IsNullOrWhiteSpace(i.Name))
            .Select(i => new Installation { Name = i.Name.Trim(), GameRoot = i.Game, ModsDir = i.Mods })
            .ToList();
        var dupe = cleaned.GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (dupe is not null)
        {
            MessageBox.Show(this, $"Two installations are both named \"{dupe.Key}\". Names must be unique.",
                "Ostrasort", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _store.Items = cleaned;
        DialogResult = true;
    }

    private void UpdateEditorEnabled()
    {
        Editor.IsEnabled = Selected is not null;
        BtnRemove.IsEnabled = Selected is not null;
    }

    private string UniqueName(string baseName)
    {
        if (_items.All(i => !string.Equals(i.Name, baseName, StringComparison.OrdinalIgnoreCase))) return baseName;
        for (var n = 2; ; n++)
        {
            var candidate = $"{baseName} {n}";
            if (_items.All(i => !string.Equals(i.Name, candidate, StringComparison.OrdinalIgnoreCase))) return candidate;
        }
    }
}
