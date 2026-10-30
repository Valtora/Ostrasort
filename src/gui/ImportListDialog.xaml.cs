using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using Microsoft.Win32;

namespace Ostrasort.Gui;

/// <summary>
/// Turns a share code, a list file or a Workshop collection into a profile for
/// this install, listing the mods it lacks with links to their Workshop pages.
/// </summary>
public partial class ImportListDialog : Window
{
    private readonly GameEnv _env;
    private readonly Analysis _analysis;
    private readonly HttpClient _http;
    private Profile? _profile;
    private string? _collectionId;

    private sealed record MissingRow(string Name, string Detail, string? WorkshopId);

    /// <summary>The name of the profile saved, when the player saved one.</summary>
    public string? SavedName { get; private set; }

    public ImportListDialog(GameEnv env, Analysis analysis, HttpClient http)
    {
        InitializeComponent();
        _env = env;
        _analysis = analysis;
        _http = http;
        Loaded += (_, _) => TxtSource.Focus();
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = $"Ostrasort mod list|*{ModListSharing.FileExtension};*.json|All files|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            TxtSource.Text = File.ReadAllText(dlg.FileName);
            Check_Click(sender, e);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Ostrasort", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        var text = TxtSource.Text.Trim();
        if (text.Length == 0) return;
        _collectionId = null;
        SharedList? list = ModListSharing.Parse(text);
        if (list is null)
        {
            if (Workshop.ParseId(text) is not { } id)
            {
                Show("That is not a share code, a mod list or a Workshop link.");
                return;
            }
            BtnCheck.IsEnabled = false;
            Show("Asking Steam for the collection…");
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var items = await Workshop.FetchCollectionAsync(_http, id, cts.Token);
                if (items is null)
                {
                    Show($"Steam does not know {id} as a collection. Paste the link of a collection, not of a single mod.");
                    return;
                }
                var details = await Workshop.FetchDetailsAsync(_http, items.Append(id).ToList(), cts.Token);
                var title = details.TryGetValue(id, out var d) && d.Title is { Length: > 0 } t ? t : $"Collection {id}";
                list = ModListSharing.FromCollection(title, items, details);
                _collectionId = id;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                Show($"Could not reach Steam: {ex.Message}");
                return;
            }
            finally { BtnCheck.IsEnabled = true; }
        }

        var name = list.Name.Length > 0 ? list.Name : "Imported list";
        var (profile, missing) = ModListSharing.Resolve(list, _env, _analysis, name);
        _profile = profile;
        TxtName.Text = name;

        var found = profile.ModCount;
        var summary = $"“{name}” lists {list.Mods.Count} mod(s). {found} are installed here";
        summary += missing.Count == 0 ? ", so nothing is missing." : $"; {missing.Count} are not:";
        if (list.GameVersion is { } gv && _env.InstalledVersion is { } iv && gv != iv)
            summary += $" (The list was made on game {gv}; you have {iv}.)";
        if (missing.Count > 0)
            summary += " Subscribe to the Workshop ones on Steam, let it download them, then press Check again. Saving now makes a profile without them.";
        Show(summary);

        ListMissing.ItemsSource = missing.Select(m => new MissingRow(m.Name,
            m.WorkshopId is { } wid ? $"Workshop item {wid}" : $"local mod “{m.Folder}”: ask whoever shared the list for it", m.WorkshopId)).ToList();
        PanelMissingButtons.Visibility = missing.Any(m => m.WorkshopId is not null) ? Visibility.Visible : Visibility.Collapsed;
        BtnOpenCollection.Visibility = _collectionId is null ? Visibility.Collapsed : Visibility.Visible;
        BtnSave.IsEnabled = found > 0;
    }

    private void Show(string text) => TxtSummary.Text = text;

    private static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no handler for the link: nothing more to do */ }
    }

    private void OpenSelected_Click(object sender, RoutedEventArgs e)
    {
        var picks = ListMissing.SelectedItems.OfType<MissingRow>().Where(r => r.WorkshopId is not null).ToList();
        if (picks.Count > 10 && MessageBox.Show(this, $"Open {picks.Count} Workshop pages?", "Ostrasort",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        foreach (var r in picks) Open(Workshop.ItemUrl(r.WorkshopId!));
    }

    private void OpenCollection_Click(object sender, RoutedEventArgs e)
    {
        if (_collectionId is not null) Open(Workshop.ItemUrl(_collectionId));
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_profile is null) return;
        var name = TxtName.Text.Trim();
        if (name.Length == 0) { TxtName.Focus(); return; }
        if (ProfileStore.Exists(_env.LoadingOrderPath, name) &&
            MessageBox.Show(this, $"A profile named “{name}” already exists. Overwrite it?", "Ostrasort",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            ProfileStore.Save(_env.LoadingOrderPath, new Profile
            {
                Name = name, SavedAt = _profile.SavedAt, SavedGameVersion = _profile.SavedGameVersion, Entries = _profile.Entries,
            });
            SavedName = name;
            DialogResult = true;
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Ostrasort", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}
