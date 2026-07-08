using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace Ostrasort.Gui;

public sealed record ModRow(string Pos, string Name, string Source, string Class, string Data,
                            string Version, string WorkshopId, string Notes, Brush Brush, string? Dir, string Tooltip,
                            ModEntry M, bool Draggable,
                            string LastUpdated, string UpdateText, Brush UpdateBrush);
public sealed record LineVm(string Text, Brush Brush, Thickness Margin, bool Bold = false)
{
    public FontWeight Weight => Bold ? FontWeights.Bold : FontWeights.Normal;
}
public sealed record ProfileRow(string Name, string Detail, Profile Profile);

public partial class MainWindow : Window
{
    // severity brushes now come from the theme (light/dark) - see ThemeManager
    private static Brush Normal => ThemeManager.Normal;
    private static Brush Dim => ThemeManager.Dim;
    private static Brush Good => ThemeManager.Good;
    private static Brush Warn => ThemeManager.Warn;
    private static Brush Bad => ThemeManager.Bad;

    private static readonly HttpClient Http = CreateHttp();
    private static HttpClient CreateHttp()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Ostrasort");
        return c;
    }

    private GameEnv _env;
    private readonly GuiSettings _settings;
    private readonly InstallationStore _installs;
    private string? _activeInstall;
    private bool _switchingInstall;
    private readonly IgnoreList _ignore = IgnoreList.LoadDefault();
    private EngineState? _state;
    private System.Collections.ObjectModel.ObservableCollection<ModRow> _rows = new();
    private bool _manualDirty;
    private bool _busy;
    private bool _ffuBannerDismissed;
    private int _attentionCount;
    private DateTime _lastScan = DateTime.MinValue;
    private Point _dragStart;
    private ModRow? _dragRow;

    // Steam Workshop publish times (published-file id -> unix seconds), fetched
    // once per session and reused across rescans so we don't re-hit Steam.
    private readonly Dictionary<string, long> _wsUpdated = new();
    private System.Threading.CancellationTokenSource? _wsCts;

    // undo/redo: disk-level operation snapshots + in-memory drag arrangements
    private readonly Stack<OpSnapshot> _undo = new();
    private readonly Stack<OpSnapshot> _redo = new();
    private readonly Stack<List<string>> _arrUndo = new();
    private readonly Stack<List<string>> _arrRedo = new();

    public MainWindow(GameEnv env, InstallationStore? installs = null, string? activeInstall = null)
    {
        _env = env;
        _settings = GuiSettings.Load();
        _installs = installs ?? InstallationStore.Load();
        _activeInstall = activeInstall;
        InitializeComponent();
        RestoreWindowState();
        CmbTheme.SelectedIndex = _settings.Theme switch { "light" => 1, "dark" => 2, _ => 0 };
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        Title = $"Ostrasort v{Program.Version}";
        ChkTidy.IsChecked = _settings.Tidy;
        if (_settings.Tab >= 0 && _settings.Tab < Tabs.Items.Count) Tabs.SelectedIndex = _settings.Tab;
        PopulateInstallations();
        OpLog.Add($"Ostrasort v{Program.Version} opened ({_env.GameRoot}).");
        Rescan();
        _ = CheckForUpdateAsync();
    }

    // ---------------------------------------------------------------- scan ---

    private void Rescan()
    {
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            _manualDirty = false;
            _arrUndo.Clear();
            _arrRedo.Clear();
            _state = Engine.Analyze(_env, ChkTidy.IsChecked == true, _ignore);
            RenderState(_state);
        }
        catch (Exception e)
        {
            MessageBox.Show(this, e.Message, "Ostrasort", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Mouse.OverrideCursor = null;
            _lastScan = DateTime.Now;
        }
    }

    // -------------------------------------------------------- installations ---

    private void PopulateInstallations()
    {
        _switchingInstall = true;
        CmbInstall.Items.Clear();
        CmbInstall.Items.Add("Auto-detect");
        foreach (var it in _installs.Items) CmbInstall.Items.Add(it.Name);
        var name = _installs.Find(_activeInstall)?.Name;   // null unless it's a real saved install
        CmbInstall.SelectedIndex = name is null ? 0 : CmbInstall.Items.IndexOf(name);
        _switchingInstall = false;
    }

    private void Install_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_switchingInstall) return;
        SwitchInstall(CmbInstall.SelectedIndex <= 0 ? null : CmbInstall.SelectedItem as string);
    }

    private void SwitchInstall(string? name)
    {
        GameEnv env;
        var inst = _installs.Find(name);
        try { env = GameEnv.Locate(inst?.Game, inst?.Mods); }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Ostrasort", MessageBoxButton.OK, MessageBoxImage.Error);
            PopulateInstallations();   // snap the combo back to the still-active install
            return;
        }
        _env = env;
        _activeInstall = name;
        _installs.Active = name;
        _installs.Save();
        OpLog.Add($"Switched to installation '{name ?? "Auto-detect"}' ({_env.GameRoot}).");
        Rescan();
    }

    private void ManageInstalls_Click(object sender, RoutedEventArgs e)
    {
        if (new InstallationsDialog(_installs, null) { Owner = this }.ShowDialog() != true)
        {
            PopulateInstallations();   // discard any visual edits the dialog made to the list
            return;
        }
        _installs.Save();
        if (_installs.Find(_activeInstall) is null) _activeInstall = null;   // active was renamed/removed
        PopulateInstallations();
        SwitchInstall(_activeInstall);   // re-resolve in case the active install's folders changed
    }

    private void RenderState(EngineState s)
    {
        var a = s.Analysis;
        RunGamePath.Text = _env.GameRoot + "  ";
        RunFoundVia.Text = $"   (found via {_env.DiscoveredVia})";
        RunModsPath.Text = _env.ModsDir + "  ";
        TxtVersions.Text = _env.InstalledVersion ?? "unknown";
        TxtCoreInfo.Text = $"{s.Scanner.CoreIndex.Count:N0} objects across {s.Scanner.CoreTypes} types" +
                           (s.Scanner.CoreProblemFiles > 0 ? $"   ({s.Scanner.CoreProblemFiles} non-standard JSON file(s))" : "");

        // mod table
        _rows = new System.Collections.ObjectModel.ObservableCollection<ModRow>();
        var all = a.Registered.Select((m, i) => (Pos: (i + 1).ToString(), Mod: m))
            .Concat(a.UnregisteredLocal.Concat(a.UnregisteredWorkshop).Select(m => (Pos: "-", Mod: m)));
        foreach (var (pos, m) in all) _rows.Add(BuildRow(pos, m));
        ModsGrid.ItemsSource = _rows;
        ApplyFilter();
        _ = RefreshWorkshopUpdatesAsync();

        RenderFfuBanner(s);
        RenderTabs(s);
        RenderProfiles();
        UpdateActionBar(s);
        RenderLogs();
    }

    /// <summary>
    /// The persistent (per-session dismissable) FFU banner: informational on a
    /// plain FFU install, alarm-red while the OstraAutoloader is active and
    /// Ostrasort is read-only.
    /// </summary>
    private void RenderFfuBanner(EngineState s)
    {
        var ctx = s.Analysis.Ffu;
        if (ctx is null || _ffuBannerDismissed)
        {
            FfuBanner.Visibility = Visibility.Collapsed;
            return;
        }
        var rival = ctx.AutoloaderActive;
        FfuBanner.Background = rival ? ThemeManager.BannerAlarmBg : ThemeManager.BannerInfoBg;
        FfuBanner.BorderBrush = rival ? ThemeManager.BannerAlarmBorder : ThemeManager.BannerInfoBorder;
        TxtFfuBannerTitle.Text = rival
            ? "OstraAutoloader active — Ostrasort is read-only on this install"
            : $"{ctx.Summary} detected — FFU ordering rules are applied";
        TxtFfuBannerTitle.Foreground = rival ? Bad : Warn;
        ListFfuBanner.ItemsSource = FfuAnalysis.Notices(s.Analysis);
        BtnDisableAutoloader.Visibility = rival ? Visibility.Visible : Visibility.Collapsed;
        var ffuRemovable = ctx.FrameworkPresent || s.Analysis.AllMods.Any(m =>
            string.Equals(m.DisplayName, FfuAnalysis.MinorFixesPlus, StringComparison.OrdinalIgnoreCase));
        BtnRemoveFfu.Visibility = ffuRemovable ? Visibility.Visible : Visibility.Collapsed;
        FfuBanner.Visibility = Visibility.Visible;
    }

    private void FfuBannerDismiss_Click(object sender, RoutedEventArgs e)
    {
        _ffuBannerDismissed = true;
        FfuBanner.Visibility = Visibility.Collapsed;
    }

    private void RemoveFfu_Click(object sender, RoutedEventArgs e)
    {
        if (_state is null || !GateRunning()) return;
        var ctx = _state.Analysis.Ffu ?? new FfuContext();
        var mfpCount = _state.Analysis.AllMods.Count(m => m.Dir is not null &&
            string.Equals(m.DisplayName, FfuAnalysis.MinorFixesPlus, StringComparison.OrdinalIgnoreCase));
        var what = new List<string>();
        if (ctx.FrameworkDllPaths.Count > 0) what.Add($"{ctx.FrameworkDllPaths.Count} FFU DLL(s)");
        if (mfpCount > 0) what.Add("Minor Fixes Plus");
        if (what.Count == 0) return;

        var choice = ParkOrDeleteDialog.Show(this,
            $"Remove FFU Core ({string.Join(" + ", what)})?",
            "Unregisters it from the load order either way (a .bak is kept). Why this is recommended is explained in the banner.");
        if (choice == ParkOrDelete.Cancel) return;
        try
        {
            CaptureOp("remove FFU Core");
            var removal = FfuAnalysis.RemoveFfuCore(_env, ctx, _state.Analysis, choice == ParkOrDelete.Delete);
            var verb = removal.Deleted ? "deleted" : "parked";
            foreach (var f in removal.Affected) OpLog.Add($"Removed FFU Core ({verb}): {f}");
            foreach (var r in removal.Unregistered) OpLog.Add($"Removed FFU Core load-order entry: {r}");
            Rescan();
            RunStatus.Text = $"FFU removed ({verb}) — Steam Workshop mods only.  ";
            RunStatus.Foreground = Good;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Ostrasort", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DisableAutoloader_Click(object sender, RoutedEventArgs e)
    {
        if (_state?.Analysis.Ffu is not { AutoloaderActive: true } ctx || !GateRunning()) return;
        var choice = ParkOrDeleteDialog.Show(this,
            $"Disable OstraAutoloader ({ctx.AutoloaderDlls.Count} DLL(s))?",
            "Ostrasort takes over the load order. r2modman users should disable it in their profile instead. The banner explains why.");
        if (choice == ParkOrDelete.Cancel) return;
        try
        {
            var affected = FfuAnalysis.DisableAutoloader(ctx, choice == ParkOrDelete.Delete);
            var verb = choice == ParkOrDelete.Delete ? "deleted" : "disabled";
            foreach (var f in affected) OpLog.Add($"OstraAutoloader {verb}: {f}");
            Rescan();
            RunStatus.Text = $"OstraAutoloader {verb} — Ostrasort now manages the load order.  ";
            RunStatus.Foreground = Good;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Ostrasort", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private ModRow BuildRow(string pos, ModEntry m)
    {
        var s = _state!;
        var cls = m.Kind == EntryKind.Core ? "core" : m.IsPatch ? "patch" : m.Class.ToString().ToLowerInvariant();
        var data = m.Kind == EntryKind.Core ? $"{s.Scanner.CoreIndex.Count:N0} objects"
                 : m.DataObjects > 0 ? $"{m.DataObjects} objs ({m.CoreOverrides} ovr)" : "-";
        var source = m.Kind switch
        {
            EntryKind.Core => "game",
            EntryKind.Workshop => "Workshop",
            EntryKind.PluginDir => "plugins",
            _ when m.IsPatch => "generated",
            _ => "local",
        };
        var notes = new List<string>();
        if (m.Ignored) notes.Add("ignored (kept unregistered)");
        else if (!m.Registered) notes.Add("NOT REGISTERED");
        if (m.Dir is null && m.Kind != EntryKind.Core) notes.Add("DEAD ENTRY");
        if (m.Disabled) notes.Add("DISABLED (skipped at load)");
        if (m.IsPatch) notes.Add("generated by Ostrasort");
        if (m.HasPatchers) notes.Add("ships BepInEx patchers");
        if (m.HasPlugins && !m.HasPatchers) notes.Add("plugins");
        if (m.IsFfuPatch) notes.Add("FFU patch — remove after one use");
        else if (m.IsFfu) notes.Add(m.FfuGroup == FfuLoadGroup.FFUCore ? "FFU core tier" : "FFU mod");
        if (m.RemoveIds.Count > 0) notes.Add($"removes {m.RemoveIds.Count} core entr{(m.RemoveIds.Count == 1 ? "y" : "ies")} (FFU removeIds)");
        if (m.GameVersionNote(_env.InstalledVersion) is { } versionNote) notes.Add(versionNote);
        if (m.JsonErrors.Count > 0) notes.Add($"{m.JsonErrors.Count} JSON problem(s)");
        var brush = m.Disabled || m.Ignored ? Dim
            : notes.Any(n => n.StartsWith("NOT") || n.StartsWith("DEAD") || n.Contains("JSON")) ? Warn : Normal;
        var name = m.Kind == EntryKind.Core ? "core (base game data)" : m.DisplayName ?? m.Name;
        var tooltip = m.Dir ?? m.Raw;
        if (m.Kind != EntryKind.Core && m.Dir is not null) tooltip += "\n(double-click to open the folder)";
        var draggable = m.Registered && m.Kind != EntryKind.Core;
        var (lastUpdated, updText, updBrush) = UpdateInfo(m);
        var version = m.Kind == EntryKind.Core ? "-" : m.ModVersion ?? "-";
        return new ModRow(pos, name, source, cls, data, version, m.WorkshopId ?? "-",
            string.Join("; ", notes), brush, m.Dir, tooltip, m, draggable,
            lastUpdated, updText, updBrush);
    }

    /// <summary>
    /// The "Last Updated" cell and its optional "update available" marker.
    /// For a subscribed Workshop mod we prefer the real publish time from the
    /// Steam Workshop (populated asynchronously into <see cref="_wsUpdated"/>)
    /// and flag it when that published version is newer than the copy on disk —
    /// Ostranauts pulls the newer files itself on the next launch, so this is
    /// purely informational. Everything else (and any Workshop mod before its
    /// publish time arrives, or while offline) falls back to the folder's own
    /// last-write time.
    /// </summary>
    private (string Date, string Upd, Brush Brush) UpdateInfo(ModEntry m)
    {
        var local = m.Dir is { } d && Directory.Exists(d) ? (DateTime?)Directory.GetLastWriteTime(d) : null;
        if (m.Kind == EntryKind.Workshop && m.WorkshopId is { } wid && _wsUpdated.TryGetValue(wid, out var tu) && tu > 0)
        {
            var published = DateTimeOffset.FromUnixTimeSeconds(tu).LocalDateTime;
            var newer = local is null || published > local.Value.AddMinutes(5);
            return (published.ToString("yyyy-MM-dd"), newer ? "⬆ update" : "", newer ? Warn : Dim);
        }
        return (local?.ToString("yyyy-MM-dd") ?? "-", "", Dim);
    }

    private void RenderTabs(EngineState s)
    {
        var a = s.Analysis;

        ListCollisions.ItemsSource = ToLineVms(CollisionView.BuildActive(a));
        ListResolved.ItemsSource = ToLineVms(CollisionView.BuildResolved(a));

        ListOrder.ItemsSource = OrderChangeView.Build(a)
            .Select(v => new LineVm(v.Text, SevBrush(v.Sev), new Thickness(v.Indent * 18, 1, 0, 1), v.Bold)).ToList();

        var pat = new List<LineVm>();
        if (!s.Patch.Exists)
            pat.Add(Patcher.HasWork(a)
                ? L("No patch generated yet — use \"Resolve conflicts & generate patch\" to merge the conflicts.", Warn)
                : L("No patch needed.", Good));
        else if (s.Patch.Stale)
        {
            foreach (var r in s.Patch.StaleReasons) pat.Add(L($"STALE — {r}", Bad));
            pat.Add(L("Regenerate it with the patch button; your previous decisions are kept where still valid.", Warn));
        }
        else if (s.Patch.Obsolete)
            pat.Add(L("The installed patch is no longer needed (conflicts resolved upstream) — remove it.", Warn));
        else
        {
            pat.Add(L($"Fresh (generated by v{s.Patch.ToolVersion ?? "?"}) — covering {string.Join(", ", s.Patch.CoveredKeys)}" +
                      (s.Patch.ExcludedCount > 0 ? $" — {s.Patch.ExcludedCount} item(s) excluded by you" : ""), Good));
            if (s.Patch.UnneededKeys.Count > 0)
                pat.Add(L($"No longer needed for {string.Join(", ", s.Patch.UnneededKeys)} (resolved upstream).", Warn));
            if (a.Collisions.Any(c => c.ResolvedByPatch && c.ObjectMergeable))
                pat.Add(L("Includes merged game objects — those are best-effort, so verify them in game.", Dim));
        }
        ListPatch.ItemsSource = pat;

        var warn = new List<LineVm>();
        foreach (var w in a.Warnings) warn.Add(L(w, Warn));
        foreach (var m in a.AllMods.Where(m => m.JsonErrors.Count > 0))
            foreach (var e in m.JsonErrors)
                warn.Add(L($"{m.Label}: {e}", Warn));
        var warnCount = warn.Count;
        if (warn.Count == 0) warn.Add(L("No warnings.", Good));
        ListWarnings.ItemsSource = warn;

        // tab highlighting: bold + orange headers on tabs that need action,
        // and the same set drives the "N things need attention" status line
        var collAttention = a.Collisions.Any(c => !c.ResolvedByPatch &&
            (c.ObjectMergeable || c.Pairs.Any(p => p.Rel is Relation.Partial or Relation.SubsetViolation)));
        var orderAttention = a.OrderChanged;
        var patchAttention = s.Patch.Stale || s.Patch.Obsolete;
        var warnAttention = warnCount > 0;

        var activeColl = a.Collisions.Count(c => !c.ResolvedByPatch);
        var resolvedColl = a.Collisions.Count - activeColl;
        SetTabHeader(TabCollisions, $"Collisions ({activeColl})", collAttention);
        SetTabHeader(TabResolved, resolvedColl == 0 ? "Resolved collisions" : $"Resolved collisions ({resolvedColl})", attention: false);
        SetTabHeader(TabOrder, a.OrderChanged ? $"Order changes ({a.Changes.Count})" : "Order changes", orderAttention);
        SetTabHeader(TabPatch, "Patch", patchAttention);
        SetTabHeader(TabWarnings, warnCount == 0 ? "Warnings" : $"Warnings ({warnCount})", warnAttention);

        _attentionCount = new[] { collAttention, orderAttention, patchAttention, warnAttention }.Count(x => x);
    }

    private void SetTabHeader(TabItem tab, string text, bool attention)
    {
        var tb = new System.Windows.Controls.TextBlock { Text = text };
        if (attention)
        {
            tb.FontWeight = FontWeights.Bold;
            tb.Foreground = Warn;
        }
        tab.Header = tb;
    }

    // Collision rendering lives in CollisionView (UI-agnostic + testable);
    // here we just map its severity to a brush.
    private List<LineVm> ToLineVms(IEnumerable<ViewLine> lines) =>
        lines.Select(v => new LineVm(v.Text, SevBrush(v.Sev),
            new Thickness(v.Indent * 18, 1, 0, 1), v.Bold)).ToList();

    private static Brush SevBrush(LineSev sev) => sev switch
    {
        LineSev.Dim => Dim,
        LineSev.Good => Good,
        LineSev.Warn => Warn,
        LineSev.Bad => Bad,
        _ => Normal,
    };

    // ---------------------------------------------------------------- logs ---

    private void RenderLogs()
    {
        if (ListLogs is null) return;   // called during XAML init before the list exists
        var idx = CmbLogSource?.SelectedIndex ?? 0;
        IEnumerable<string> lines = idx switch
        {
            1 => OpLog.Tail(GameEnv.PlayerLogPath, 300),
            2 => OpLog.Tail(_env.BepInExLogPath, 300),
            _ => OpLog.Recent(),
        };
        var brush = idx == 0 ? Normal : Dim;
        var vms = lines.Select(t => new LineVm(t, LineColour(t, brush), new Thickness(0, 0, 0, 0))).ToList();
        if (vms.Count == 0) vms.Add(new LineVm("(nothing logged yet)", Dim, new Thickness(0)));
        ListLogs.ItemsSource = vms;
        LogScroller?.ScrollToBottom();
    }

    private static Brush LineColour(string line, Brush fallback)
    {
        if (line.Contains("ERROR", StringComparison.OrdinalIgnoreCase) || line.Contains("Exception")) return Bad;
        if (line.Contains("WARN", StringComparison.OrdinalIgnoreCase)) return Warn;
        return fallback;
    }

    private void LogSource_Changed(object sender, SelectionChangedEventArgs e) => RenderLogs();
    private void RefreshLog_Click(object sender, RoutedEventArgs e) => RenderLogs();

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        if (ListLogs.ItemsSource is IEnumerable<LineVm> vms)
            Clipboard.SetText(string.Join(Environment.NewLine, vms.Select(v => v.Text)));
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        var idx = CmbLogSource?.SelectedIndex ?? 0;
        var (label, path) = idx switch
        {
            1 => ("the game log (Player.log)", GameEnv.PlayerLogPath),
            2 => ("the BepInEx log", _env.BepInExLogPath),
            _ => ("the Ostrasort operations log", OpLog.FilePath),
        };
        if (MessageBox.Show(this, $"Clear {label}? This empties the file on disk and cannot be undone.",
                "Ostrasort", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        if (idx == 0)
            OpLog.Clear();
        else if (!OpLog.ClearFile(path))
            MessageBox.Show(this, $"Could not clear {path}.", "Ostrasort",
                MessageBoxButton.OK, MessageBoxImage.Error);

        RenderLogs();
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        var path = (CmbLogSource?.SelectedIndex ?? 0) switch
        {
            1 => GameEnv.PlayerLogPath,
            2 => _env.BepInExLogPath,
            _ => OpLog.FilePath,
        };
        if (File.Exists(path))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void UpdateActionBar(EngineState s)
    {
        var a = s.Analysis;
        var running = GameEnv.IsGameRunning();
        // OstraAutoloader regenerates loading_order.json every launch - while it
        // is installed every write is futile, so the GUI is analysis-only
        var rivalLock = a.Ffu is { AutoloaderActive: true };
        var bak = _env.LoadingOrderPath + ".bak";

        BtnApply.IsEnabled = (_manualDirty || a.OrderChanged) && !running && !rivalLock;
        BtnApply.Content = _manualDirty ? "Apply manual order" : "Apply Suggested Fixes";
        BtnPatch.IsEnabled = Patcher.HasWork(a) && !running && !rivalLock;
        BtnPatch.Content = s.Patch.Stale ? "Refresh patch (decisions kept)" : "Resolve conflicts & generate patch";
        BtnUnpatch.IsEnabled = s.Patch.Exists && !running && !rivalLock;
        BtnPatchFresh.IsEnabled = BtnPatch.IsEnabled;
        BtnPatchDelete.IsEnabled = BtnUnpatch.IsEnabled;
        var backupCount = (File.Exists(bak) ? 1 : 0) + Backups.List(_env.LoadingOrderPath).Count;
        BtnRestoreBak.IsEnabled = backupCount > 0 && !running && !rivalLock;
        BtnRestoreBak.ToolTip = backupCount > 0
            ? $"Restore loading_order.json from the .bak or one of the {Backups.Keep} rolling backups ({backupCount} restore point(s) available)"
            : "No backups exist yet - one is kept on every write";
        BtnLaunch.IsEnabled = !running;
        LinkReset.IsEnabled = _manualDirty;

        var arrUndoable = _manualDirty && _arrUndo.Count > 0;
        var arrRedoable = _arrRedo.Count > 0;
        BtnUndo.IsEnabled = arrUndoable || (_undo.Count > 0 && !running && !rivalLock);
        BtnRedo.IsEnabled = arrRedoable || (_redo.Count > 0 && !running && !rivalLock);
        BtnUndo.ToolTip = arrUndoable ? "Undo row move (Ctrl+Z)"
            : _undo.Count > 0 ? $"Undo: {_undo.Peek().Label} (Ctrl+Z)" : "Nothing to undo";
        BtnRedo.ToolTip = arrRedoable ? "Redo row move (Ctrl+Y)"
            : _redo.Count > 0 ? $"Redo: {_redo.Peek().Label} (Ctrl+Y)" : "Nothing to redo";
        TxtDragHint.Text = _manualDirty ? "manual arrangement pending — apply or reset" : "drag rows to reorder manually";

        if (running)
        {
            RunStatus.Text = "Ostranauts is RUNNING — writes disabled, rescan after closing it.  ";
            RunStatus.Foreground = Bad;
        }
        else if (rivalLock)
        {
            RunStatus.Text = "OstraAutoloader manages this install — analysis only, writes disabled (see the banner).  ";
            RunStatus.Foreground = Bad;
        }
        else if (_manualDirty)
        {
            var issues = Analysis.ValidateOrder(RegisteredEntriesInVisualOrder());
            RunStatus.Text = issues.Count == 0
                ? "Manual arrangement pending — no rule violations.  "
                : $"Manual arrangement pending — {issues.Count} rule issue(s), see tooltip.  ";
            RunStatus.Foreground = issues.Count == 0 ? Warn : Bad;
            ((TextBlock)RunStatus.Parent!).ToolTip = issues.Count == 0 ? null : string.Join("\n", issues);
        }
        else if (_attentionCount > 0)
        {
            RunStatus.Text = $"{_attentionCount} thing(s) need attention — see the highlighted tabs.  ";
            RunStatus.Foreground = Warn;
            ((TextBlock)RunStatus.Parent!).ToolTip = null;
        }
        else
        {
            RunStatus.Text = "Everything satisfied — nothing to do.  ";
            RunStatus.Foreground = Good;
            ((TextBlock)RunStatus.Parent!).ToolTip = null;
        }
        LinkReset.Inlines.Clear();
        LinkReset.Inlines.Add(_manualDirty ? "reset arrangement" : "");

        UpdateProfileButtons();
    }

    private static LineVm L(string text, Brush brush, int indent = 0, bool bold = false) =>
        new(text, brush, new Thickness(indent * 18, 1, 0, 1), bold);

    // ------------------------------------------------------------- filtering ---

    private void ApplyFilter()
    {
        var view = CollectionViewSource.GetDefaultView(_rows);
        var needle = TxtFilter.Text.Trim();
        view.Filter = string.IsNullOrEmpty(needle)
            ? null
            : o => o is ModRow r &&
                   (r.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                    || r.WorkshopId.Contains(needle, StringComparison.OrdinalIgnoreCase)
                    || r.Class.Contains(needle, StringComparison.OrdinalIgnoreCase)
                    || r.Source.Contains(needle, StringComparison.OrdinalIgnoreCase)
                    || r.Notes.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    private void Filter_Changed(object sender, TextChangedEventArgs e)
    {
        if (_rows.Count > 0) ApplyFilter();
    }

    // ------------------------------------------------------------ drag-drop ---

    private static DataGridRow? RowUnder(DependencyObject? d)
    {
        while (d is not null and not DataGridRow) d = VisualTreeHelper.GetParent(d);
        return d as DataGridRow;
    }

    private void ModsGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragRow = RowUnder(e.OriginalSource as DependencyObject)?.Item as ModRow;
    }

    private void ModsGrid_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragRow is null) return;
        if (!string.IsNullOrEmpty(TxtFilter.Text)) return;            // no reordering a filtered view
        if (!_dragRow.Draggable) return;
        var delta = e.GetPosition(null) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        DragDrop.DoDragDrop(ModsGrid, _dragRow, DragDropEffects.Move);
        _dragRow = null;
    }

    private void ModsGrid_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(ModRow)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void ModsGrid_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(ModRow)) is not ModRow dragged || !dragged.Draggable) return;
        var target = RowUnder(e.OriginalSource as DependencyObject)?.Item as ModRow;
        if (target is null || ReferenceEquals(target, dragged)) return;

        var from = _rows.IndexOf(dragged);
        var to = _rows.IndexOf(target);
        if (from < 0 || to < 0) return;

        // clamp into the registered segment, below core
        var lastRegistered = _rows.Select((r, i) => (r, i)).Where(x => x.r.M.Registered).Max(x => x.i);
        to = Math.Clamp(to, 1, lastRegistered);
        if (to == from) return;

        _arrUndo.Push(CurrentArrangement());
        _arrRedo.Clear();
        _rows.RemoveAt(from);
        _rows.Insert(to, dragged);
        Renumber();
        _manualDirty = true;
        if (_state is not null) UpdateActionBar(_state);
    }

    private List<string> CurrentArrangement() =>
        _rows.Where(r => r.M.Registered).Select(r => r.M.Raw).ToList();

    private void ApplyArrangement(List<string> raws)
    {
        var byRaw = _rows.Where(r => r.M.Registered).ToDictionary(r => r.M.Raw, r => r);
        var unregistered = _rows.Where(r => !r.M.Registered).ToList();
        _rows = new System.Collections.ObjectModel.ObservableCollection<ModRow>(
            raws.Where(byRaw.ContainsKey).Select(r => byRaw[r]).Concat(unregistered));
        ModsGrid.ItemsSource = _rows;
        ApplyFilter();
        Renumber();
        _manualDirty = _state is null || !raws.SequenceEqual(_state.Analysis.Registered.Select(m => m.Raw));
        if (_state is not null) UpdateActionBar(_state);
    }

    private void Renumber()
    {
        var n = 0;
        for (var i = 0; i < _rows.Count; i++)
            if (_rows[i].M.Registered)
                _rows[i] = _rows[i] with { Pos = (++n).ToString() };
    }

    private List<ModEntry> RegisteredEntriesInVisualOrder() =>
        _rows.Where(r => r.M.Registered).Select(r => r.M).ToList();

    private void ResetManual_Click(object sender, RoutedEventArgs e)
    {
        if (_manualDirty) Rescan();
    }

    // --------------------------------------------------------------- actions ---

    private bool GateRunning()
    {
        if (!GameEnv.IsGameRunning()) return true;
        MessageBox.Show(this, "Ostranauts is running — close it first. Nothing was written.",
            "Ostrasort", MessageBoxButton.OK, MessageBoxImage.Warning);
        Rescan();
        return false;
    }

    private void Rescan_Click(object sender, RoutedEventArgs e) => Rescan();

    // ------------------------------------------------------------ undo/redo ---

    /// <summary>Snapshot the install right before a mutating operation.</summary>
    private void CaptureOp(string label)
    {
        try
        {
            _undo.Push(UndoOps.Capture(_env, label));
            _redo.Clear();
        }
        catch { /* a failed snapshot must not block the operation itself */ }
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_manualDirty && _arrUndo.Count > 0)
        {
            _arrRedo.Push(CurrentArrangement());
            ApplyArrangement(_arrUndo.Pop());
            return;
        }
        if (_undo.Count == 0 || !GateRunning()) return;
        _busy = true;
        try
        {
            var snap = _undo.Pop();
            _redo.Push(UndoOps.Capture(_env, snap.Label));
            UndoOps.Restore(_env, snap);
            OpLog.Add($"Undid: {snap.Label}.");
            Rescan();
            RunStatus.Text = $"Undid: {snap.Label}.  ";
            RunStatus.Foreground = Good;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Ostrasort", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _busy = false; }
    }

    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        if (_arrRedo.Count > 0)
        {
            _arrUndo.Push(CurrentArrangement());
            ApplyArrangement(_arrRedo.Pop());
            return;
        }
        if (_redo.Count == 0 || !GateRunning()) return;
        _busy = true;
        try
        {
            var snap = _redo.Pop();
            _undo.Push(UndoOps.Capture(_env, snap.Label));
            UndoOps.Restore(_env, snap);
            OpLog.Add($"Redid: {snap.Label}.");
            Rescan();
            RunStatus.Text = $"Redid: {snap.Label}.  ";
            RunStatus.Foreground = Good;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Ostrasort", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _busy = false; }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && Keyboard.FocusedElement is not TextBox)
        {
            if (e.Key == Key.Z && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { Redo_Click(sender, e); e.Handled = true; }
            else if (e.Key == Key.Z) { Undo_Click(sender, e); e.Handled = true; }
            else if (e.Key == Key.Y) { Redo_Click(sender, e); e.Handled = true; }
        }
    }

    private void Tidy_Changed(object sender, RoutedEventArgs e)
    {
        _settings.Tidy = ChkTidy.IsChecked == true;
        if (_state is not null && !_manualDirty) Rescan();
    }

    // --------------------------------------------------------------- theming ---

    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;   // ignore the programmatic set during construction
        var mode = CmbTheme.SelectedIndex switch { 1 => "light", 2 => "dark", _ => "system" };
        _settings.Theme = mode;
        ThemeManager.Apply(mode);
        ReapplyTheme();
        OpLog.Add($"Theme set to {mode}.");
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        // follow the OS live while the theme is set to "system"
        if (e.Category == UserPreferenceCategory.General && _settings.Theme == "system")
            Dispatcher.Invoke(() => { ThemeManager.Apply("system"); ReapplyTheme(); });
    }

    /// <summary>Re-apply theme brushes to everything set in code, preserving the current rows and any manual arrangement.</summary>
    private void ReapplyTheme()
    {
        if (_state is null) return;
        for (var i = 0; i < _rows.Count; i++) _rows[i] = BuildRow(_rows[i].Pos, _rows[i].M);
        RenderTabs(_state);
        RenderFfuBanner(_state);
        RenderLogs();
        RenderProfiles();
        UpdateActionBar(_state);
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_state is null || !GateRunning()) return;
        _busy = true;
        try
        {
            List<string> newOrder;
            if (_manualDirty)
            {
                var ordered = RegisteredEntriesInVisualOrder();
                var issues = Analysis.ValidateOrder(ordered);
                if (issues.Any(i => i.StartsWith("BLOCK:")))
                {
                    MessageBox.Show(this, string.Join("\n", issues), "Ostrasort",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                if (issues.Count > 0 && MessageBox.Show(this,
                        "This arrangement has rule warnings:\n\n" + string.Join("\n", issues) +
                        "\n\nApply it anyway?", "Ostrasort",
                        MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return;
                newOrder = ordered.Select(m => m.Raw).ToList();
            }
            else
            {
                newOrder = _state.Analysis.SuggestedOrder;
            }

            CaptureOp(_manualDirty ? "apply manual order" : "apply suggested fixes");
            LoadOrderFile.Read(_env.LoadingOrderPath).Write(newOrder);
            OpLog.Add($"Applied {(_manualDirty ? "manual" : "suggested")} load order ({newOrder.Count} entries).");
            Rescan();
            MessageBox.Show(this, "Load order applied. The previous file was saved as loading_order.json.bak.",
                "Ostrasort", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Ostrasort", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _busy = false; }
    }

    private void Patch_Click(object sender, RoutedEventArgs e) => GeneratePatch(fresh: false);

    private void PatchFresh_Click(object sender, RoutedEventArgs e)
    {
        if (_state is { Patch.Exists: true } &&
            MessageBox.Show(this,
                "Discard ALL stored decisions (source picks and exclusions) and resolve every conflict again from scratch?",
                "Ostrasort", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        GeneratePatch(fresh: true);
    }

    private void GeneratePatch(bool fresh)
    {
        if (_state is null || !GateRunning()) return;
        _busy = true;
        try
        {
            var plan = Patcher.PlanMerge(_env, _state.Analysis, fresh);
            if (plan.IsEmpty)
            {
                MessageBox.Show(this, "There are no mergeable conflicts.",
                    "Ostrasort", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (plan.ContestedItems.Any())
            {
                var dlg = new ResolverDialog(plan) { Owner = this };
                if (dlg.ShowDialog() != true) return;
            }
            CaptureOp(fresh ? "rebuild patch from scratch"
                    : _state.Patch.Exists ? "refresh patch (incl. decisions)" : "generate patch");
            var result = Patcher.Generate(_env, plan, _state.Analysis, _env.InstalledVersion, Program.Version);
            OpLog.Add($"Generated Ostrasort Patch: {string.Join("; ", result.Merged)}.");
            foreach (var w in result.SchemaWarnings) OpLog.Add($"  patch schema warning: {w}");
            Rescan();
            var msg = "Patch generated and registered after everything it merges.";
            if (result.SchemaWarnings.Count > 0)
                msg += "\n\nSome merged objects did not fully match the game's schemas (best-effort — " +
                       "verify in game). See the Patch tab for details:\n• " +
                       string.Join("\n• ", result.SchemaWarnings.Take(6));
            MessageBox.Show(this, msg, "Ostrasort", MessageBoxButton.OK,
                result.SchemaWarnings.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Ostrasort", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _busy = false; }
    }

    private void Unpatch_Click(object sender, RoutedEventArgs e) => RemovePatch();

    /// <summary>
    /// Deletes the generated Ostrasort Patch (folder + load-order entry) after a
    /// confirmation. Shared by the Patch tab's Remove button and the mod table's
    /// right-click Remove, so the patch can be removed like any other mod.
    /// </summary>
    private void RemovePatch()
    {
        if (_state is null || !GateRunning()) return;
        if (MessageBox.Show(this, "Remove the generated Ostrasort Patch (folder + load-order entry)?\n\n" +
                "It is safe to remove - regenerate it any time with “Resolve conflicts & generate patch”.",
                "Ostrasort", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _busy = true;
        try
        {
            CaptureOp("remove patch");
            Patcher.Remove(_env);
            OpLog.Add("Removed the Ostrasort Patch.");
            Rescan();
            RunStatus.Text = "Ostrasort Patch removed (folder + load-order entry).  ";
            RunStatus.Foreground = Good;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Ostrasort", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _busy = false; }
    }

    /// <summary>Snapshots the current loading_order.json into the rolling history on demand (safe anytime - LOCALAPPDATA only).</summary>
    private void MakeBackup_Click(object sender, RoutedEventArgs e)
    {
        var live = _env.LoadingOrderPath;
        if (!File.Exists(live))
        {
            MessageBox.Show(this, "There is no loading_order.json to back up yet — launch the game once so it creates one.",
                "Ostrasort", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            Backups.Snapshot(live, File.ReadAllText(live));
            OpLog.Add("Backed up the current loading_order.json (manual).");
            if (_state is not null) UpdateActionBar(_state);   // refresh the Restore button's count/tooltip
            var count = (File.Exists(live + ".bak") ? 1 : 0) + Backups.List(live).Count;
            RunStatus.Text = $"Backup created — {count} restore point(s) available.  ";
            RunStatus.Foreground = Good;
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Ostrasort", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    /// <summary>Offers every available restore point: the sibling .bak plus the rolling backups.</summary>
    private void RestoreBak_Click(object sender, RoutedEventArgs e)
    {
        var bak = _env.LoadingOrderPath + ".bak";
        var menu = new ContextMenu { PlacementTarget = BtnRestoreBak };
        if (File.Exists(bak))
        {
            var item = new MenuItem
            {
                Header = $".bak — {File.GetLastWriteTime(bak):yyyy-MM-dd HH:mm:ss}   (last write; restore swaps, so it's reversible)",
            };
            item.Click += (_, _) => RestoreSwapBak();
            menu.Items.Add(item);
        }
        foreach (var (path, written) in Backups.List(_env.LoadingOrderPath))
        {
            var item = new MenuItem { Header = $"backup — {written:yyyy-MM-dd HH:mm:ss}" };
            var p = path;
            item.Click += (_, _) => RestoreFromBackup(p);
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) return;   // button should be disabled in this case anyway
        menu.IsOpen = true;
    }

    private void RestoreSwapBak()
    {
        if (!GateRunning()) return;
        var live = _env.LoadingOrderPath;
        var bak = live + ".bak";
        if (!File.Exists(bak)) return;
        _busy = true;
        try
        {
            var bakText = File.ReadAllText(bak);
            if (!bakText.TrimStart().StartsWith('['))
            {
                MessageBox.Show(this, "The .bak is not a top-level JSON array — refusing to restore it.",
                    "Ostrasort", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            if (MessageBox.Show(this,
                    $"Swap loading_order.json with the backup from {File.GetLastWriteTime(bak):yyyy-MM-dd HH:mm:ss}?\n\n" +
                    "The current file becomes the new .bak, so restoring is reversible.",
                    "Ostrasort", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            CaptureOp("restore .bak");
            var liveText = File.ReadAllText(live);
            Backups.Snapshot(live, liveText);
            AtomicFile.WriteAllText(live, bakText);
            File.WriteAllText(bak, liveText);
            OpLog.Add("Restored loading_order.json from the .bak (swapped).");
            Rescan();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Ostrasort", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _busy = false; }
    }

    private void RestoreFromBackup(string backupPath)
    {
        if (!GateRunning()) return;
        _busy = true;
        try
        {
            var text = File.ReadAllText(backupPath);
            if (!text.TrimStart().StartsWith('['))
            {
                MessageBox.Show(this, "That backup is not a top-level JSON array — refusing to restore it.",
                    "Ostrasort", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            if (MessageBox.Show(this,
                    $"Restore loading_order.json from the backup written {File.GetLastWriteTime(backupPath):yyyy-MM-dd HH:mm:ss}?\n\n" +
                    "The current file becomes the new .bak first, so this is reversible.",
                    "Ostrasort", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            CaptureOp("restore backup");
            var live = _env.LoadingOrderPath;
            var liveText = File.ReadAllText(live);
            File.WriteAllText(live + ".bak", liveText);
            Backups.Snapshot(live, liveText);
            AtomicFile.WriteAllText(live, text);
            OpLog.Add($"Restored loading_order.json from backup {Path.GetFileName(backupPath)}.");
            Rescan();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Ostrasort", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _busy = false; }
    }

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("steam://rungameid/1022980") { UseShellExecute = true });
            OpLog.Add("Launched Ostranauts via Steam.");
            RunStatus.Text = "Launching Ostranauts via Steam…  ";
            RunStatus.Foreground = Dim;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Ostrasort", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ------------------------------------------------------- report export ---

    private void CopyReport_Click(object sender, RoutedEventArgs e)
    {
        if (_state is null) return;
        Clipboard.SetText(MarkdownReport.Build(_env, _state, Program.Version));
        RunStatus.Text = "Report copied to the clipboard as Markdown.  ";
        RunStatus.Foreground = Good;
    }

    private void SaveReport_Click(object sender, RoutedEventArgs e)
    {
        if (_state is null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = "ostrasort-report.md",
            Filter = "Markdown (*.md)|*.md|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        File.WriteAllText(dlg.FileName, MarkdownReport.Build(_env, _state, Program.Version));
    }

    // ---------------------------------------------------------- context menu ---

    private void ModsGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (RowUnder(e.OriginalSource as DependencyObject) is { } row) row.IsSelected = true;
    }

    private void RowMenu_Opened(object sender, RoutedEventArgs e)
    {
        var row = ModsGrid.SelectedItem as ModRow;
        MenuOpenFolder.IsEnabled = row?.Dir is not null;
        MenuOpenWorkshop.IsEnabled = row?.M.WorkshopId is not null;
        MenuCopyName.IsEnabled = row is not null;
        MenuCopyId.IsEnabled = row?.M.WorkshopId is not null;

        // enable/disable toggles the vanilla |disabled marker (registered,
        // non-core, non-patch entries; not while the autoloader owns the file)
        var rivalLock = _state?.Analysis.Ffu is { AutoloaderActive: true };
        var canToggle = !rivalLock && row?.M is { Registered: true, Kind: not EntryKind.Core } && !row.M.IsPatch;
        Show(MenuDisable, canToggle && row!.M is { Disabled: false });
        Show(MenuEnable, canToggle && row!.M is { Disabled: true });

        // register: any discovered-but-unregistered mod (local, plugins-dir, or a
        // subscribed Workshop item) - adds it to aLoadOrder in its suggested slot.
        // The patch is excluded: it registers itself when generated.
        Show(MenuRegister, !rivalLock && row?.M is { Registered: false, Kind: not EntryKind.Core, IsPatch: false });

        // ignore applies to discovered-but-unregistered local / plugins-dir mods
        var canIgnore = row?.M is { Registered: false, Kind: EntryKind.Local or EntryKind.PluginDir };
        Show(MenuIgnore, canIgnore && row!.M is { Ignored: false });
        Show(MenuUnignore, canIgnore && row!.M is { Ignored: true });

        // removal: local/plugins-dir folders (park or delete), the generated
        // patch (its own guarded removal, regenerable), or Workshop (Steam owns
        // the files - offer Unsubscribe instead)
        var isPatch = row?.M.IsPatch == true;
        Show(MenuDeleteMod, !rivalLock && (isPatch
            ? _state?.Patch.Exists == true
            : row?.M is { Kind: EntryKind.Local or EntryKind.PluginDir, Dir: not null }));
        MenuDeleteMod.Header = isPatch ? "Remove generated patch…" : "Remove mod (park or delete)…";
        MenuDeleteMod.ToolTip = isPatch
            ? "Delete the generated OstrasortPatch folder and drop its load-order entry. Safe to remove - regenerate it any time with “Resolve conflicts & generate patch”."
            : "Park the mod folder as *.disabled (reversible) or delete it permanently, and drop its load-order entry either way. Deleted files are NOT restored by Ostrasort's undo.";
        Show(MenuUnsubscribe, row?.M is { Kind: EntryKind.Workshop, WorkshopId: not null });

        MenuStateSeparator.Visibility =
            MenuDisable.Visibility == Visibility.Visible || MenuEnable.Visibility == Visibility.Visible ||
            MenuRegister.Visibility == Visibility.Visible ||
            MenuIgnore.Visibility == Visibility.Visible || MenuUnignore.Visibility == Visibility.Visible ||
            MenuDeleteMod.Visibility == Visibility.Visible || MenuUnsubscribe.Visibility == Visibility.Visible
                ? Visibility.Visible : Visibility.Collapsed;

        static void Show(MenuItem item, bool visible) =>
            item.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MenuDisable_Click(object sender, RoutedEventArgs e) => ToggleDisabled(disable: true);
    private void MenuEnable_Click(object sender, RoutedEventArgs e) => ToggleDisabled(disable: false);

    /// <summary>Adds/removes the game's own |disabled marker on the selected entry (guarded write, undoable).</summary>
    private void ToggleDisabled(bool disable)
    {
        if (_state is null || ModsGrid.SelectedItem is not ModRow row || !GateRunning()) return;
        var m = row.M;
        var name = m.DisplayName ?? m.Name;
        _busy = true;
        try
        {
            CaptureOp(disable ? $"disable {name}" : $"enable {name}");
            var lo = LoadOrderFile.Read(_env.LoadingOrderPath);
            var updated = lo.Order.Select(x => x == m.Raw ? m.RawToggledDisabled(disable) : x).ToList();
            lo.Write(updated);
            OpLog.Add(disable
                ? $"Disabled '{name}' ({m.Raw} -> {m.RawToggledDisabled(true)})."
                : $"Enabled '{name}' ({m.Raw} -> {m.RawToggledDisabled(false)}).");
            Rescan();
            RunStatus.Text = disable
                ? $"'{name}' disabled — the entry stays, the game skips it.  "
                : $"'{name}' enabled.  ";
            RunStatus.Foreground = Good;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Ostrasort", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _busy = false; }
    }

    /// <summary>Registers the selected unregistered mod into loading_order.json in its suggested slot (guarded write, undoable).</summary>
    private void MenuRegister_Click(object sender, RoutedEventArgs e)
    {
        if (_state is null || ModsGrid.SelectedItem is not ModRow row || !GateRunning()) return;
        var m = row.M;
        var name = m.DisplayName ?? m.Name;
        _busy = true;
        try
        {
            CaptureOp($"register {name}");
            var result = ModRegistration.Register(_env, m, _state.Analysis);
            _ignore.Remove(IgnoreList.KeyFor(_env, m));   // registering overrides any "leave unregistered" preference
            OpLog.Add(result.AlreadyRegistered
                ? $"'{name}' was already registered ({result.Entry}) - no change."
                : $"Registered '{name}' ({result.Entry}) at position {result.Position + 1}.");
            Rescan();
            RunStatus.Text = result.AlreadyRegistered
                ? $"'{name}' was already registered.  "
                : $"'{name}' registered — the game will now load it.  ";
            RunStatus.Foreground = Good;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Ostrasort", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _busy = false; }
    }

    private void MenuIgnore_Click(object sender, RoutedEventArgs e) => SetIgnored(true);
    private void MenuUnignore_Click(object sender, RoutedEventArgs e) => SetIgnored(false);

    /// <summary>Park a local/plugins-dir mod as *.disabled or delete it, and drop its load-order entry.</summary>
    private void MenuDeleteMod_Click(object sender, RoutedEventArgs e)
    {
        if (_state is null || ModsGrid.SelectedItem is not ModRow row || !GateRunning()) return;
        var m = row.M;
        // the generated patch is Ostrasort's own - it has no park option and its
        // own guarded removal (validates the marker, drops the entry, regenerable)
        if (m.IsPatch) { RemovePatch(); return; }
        var name = m.DisplayName ?? m.Name;
        var choice = ParkOrDeleteDialog.Show(this,
            $"Remove local mod '{name}'?",
            "Park renames its folder to *.disabled (rename back to restore); Delete removes the files " +
            "permanently — Ostrasort's undo does NOT bring deleted files back. Its load-order entry is " +
            "dropped either way (a .bak and a rolling backup are kept).");
        if (choice == ParkOrDelete.Cancel) return;
        _busy = true;
        try
        {
            CaptureOp($"remove mod {name}");
            var result = ModRemoval.RemoveLocal(_env, m, choice == ParkOrDelete.Delete);
            _ignore.Remove(IgnoreList.KeyFor(_env, m));   // drop any now-stale ignore preference
            var verb = result.Deleted ? "deleted" : "parked";
            foreach (var f in result.Affected) OpLog.Add($"Removed mod '{name}' ({verb}): {f}");
            foreach (var r in result.Unregistered) OpLog.Add($"Removed load-order entry: {r}");
            Rescan();
            RunStatus.Text = result.Deleted
                ? $"'{name}' deleted and unregistered.  "
                : $"'{name}' parked as .disabled and unregistered — rename the folder back to restore it.  ";
            RunStatus.Foreground = Good;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Ostrasort", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _busy = false; }
    }

    /// <summary>
    /// True unsubscribing needs an authenticated Steamworks session under the
    /// game's app id, so Steam keeps ownership: open the item's page in the
    /// Steam client, where Unsubscribe is one click.
    /// </summary>
    private void MenuUnsubscribe_Click(object sender, RoutedEventArgs e)
    {
        if (ModsGrid.SelectedItem is not ModRow { M: { Kind: EntryKind.Workshop, WorkshopId: { } id } m }) return;
        try
        {
            Process.Start(new ProcessStartInfo($"steam://url/CommunityFilePage/{id}") { UseShellExecute = true });
            OpLog.Add($"Opened Steam page for {m.DisplayName ?? id} [{id}] to unsubscribe.");
            RunStatus.Text = "Steam opened — press Unsubscribe on the item's page, then rescan here.  ";
            RunStatus.Foreground = Dim;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Ostrasort", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Persists "leave this folder unregistered" (no game files touched).</summary>
    private void SetIgnored(bool ignored)
    {
        if (ModsGrid.SelectedItem is not ModRow row) return;
        var key = IgnoreList.KeyFor(_env, row.M);
        if (ignored) _ignore.Add(key); else _ignore.Remove(key);
        OpLog.Add(ignored
            ? $"Ignoring unregistered mod '{row.M.DisplayName ?? row.M.Name}' (kept unregistered on purpose)."
            : $"Stopped ignoring '{row.M.DisplayName ?? row.M.Name}'.");
        Rescan();
    }

    private void MenuOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ModsGrid.SelectedItem is ModRow { Dir: not null } row) OpenFolder(row.Dir);
    }

    private void MenuOpenWorkshop_Click(object sender, RoutedEventArgs e)
    {
        if (ModsGrid.SelectedItem is ModRow { M.WorkshopId: { } id })
            Process.Start(new ProcessStartInfo($"https://steamcommunity.com/sharedfiles/filedetails/?id={id}")
            { UseShellExecute = true });
    }

    private void MenuCopyName_Click(object sender, RoutedEventArgs e)
    {
        if (ModsGrid.SelectedItem is ModRow row) Clipboard.SetText(row.Name);
    }

    private void MenuCopyId_Click(object sender, RoutedEventArgs e)
    {
        if (ModsGrid.SelectedItem is ModRow { M.WorkshopId: { } id }) Clipboard.SetText(id);
    }

    // --------------------------------------------------------------- profiles ---

    private void RenderProfiles()
    {
        if (ListProfiles is null) return;
        var selectedName = (ListProfiles.SelectedItem as ProfileRow)?.Name;
        var rows = ProfileStore.List(_env.LoadingOrderPath)
            .Select(p => new ProfileRow(p.Name, DetailFor(p), p)).ToList();
        ListProfiles.ItemsSource = rows;
        if (selectedName is not null)
            ListProfiles.SelectedItem = rows.FirstOrDefault(r => string.Equals(r.Name, selectedName, StringComparison.OrdinalIgnoreCase));
        UpdateProfileButtons();
    }

    private static string DetailFor(Profile p)
    {
        var bits = new List<string> { $"{p.ModCount} mod(s)" };
        if (DateTime.TryParse(p.SavedAt, out var d)) bits.Add($"saved {d:yyyy-MM-dd HH:mm}");
        if (p.SavedGameVersion is { Length: > 0 } v) bits.Add($"game {v}");
        return string.Join("   ·   ", bits);
    }

    /// <summary>Save is always available (read-only); switching writes, so it follows the game/rival gates.</summary>
    private void UpdateProfileButtons()
    {
        if (BtnProfileSave is null) return;   // called from UpdateActionBar during XAML init
        var running = GameEnv.IsGameRunning();
        var rivalLock = _state?.Analysis.Ffu is { AutoloaderActive: true };
        var hasSel = ListProfiles?.SelectedItem is ProfileRow;
        BtnProfileSave.IsEnabled = _state is not null && !_manualDirty;
        BtnProfileSwitch.IsEnabled = hasSel && !running && !rivalLock;
        BtnProfileRename.IsEnabled = hasSel;
        BtnProfileDelete.IsEnabled = hasSel;
        TxtProfileHint.Text =
            _manualDirty ? "apply or reset your manual arrangement before saving a profile"
            : running ? "close the game to switch profiles"
            : rivalLock ? "OstraAutoloader manages this install — switching is disabled"
            : (ListProfiles?.Items.Count ?? 0) == 0 ? "no profiles yet — “Save current…” makes one from the live order"
            : "";
    }

    private void Profiles_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateProfileButtons();

    private void Profiles_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ListProfiles.SelectedItem is ProfileRow) ProfileSwitch_Click(sender, e);
    }

    private void ProfileSave_Click(object sender, RoutedEventArgs e)
    {
        if (_state is null) return;
        if (_manualDirty)
        {
            MessageBox.Show(this, "You have an unapplied manual arrangement. Apply or reset it first, then save the " +
                "profile from the on-disk order.", "Ostrasort", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var name = PromptDialog.Ask(this, "Name this profile (saves the current load order):");
        if (name is null) return;
        if (ProfileStore.Exists(_env.LoadingOrderPath, name) &&
            MessageBox.Show(this, $"A profile named “{name}” already exists. Overwrite it?",
                "Ostrasort", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        try
        {
            var profile = Profile.Capture(_state.Analysis, name, _env.InstalledVersion, DateTime.Now.ToString("o"));
            ProfileStore.Save(_env.LoadingOrderPath, profile);
            OpLog.Add($"Saved profile '{name}' ({profile.ModCount} mods).");
            RenderProfiles();
            SelectProfile(name);
            RunStatus.Text = $"Profile '{name}' saved.  ";
            RunStatus.Foreground = Good;
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Ostrasort", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void ProfileSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (_state is null || ListProfiles.SelectedItem is not ProfileRow row || !GateRunning()) return;
        var dlg = new ProfileSwitchDialog(_env, _state.Analysis, row.Profile) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result is not { } plan) return;
        _busy = true;
        try
        {
            CaptureOp($"switch to profile '{row.Name}'");
            LoadOrderFile.Read(_env.LoadingOrderPath).Write(plan.NewOrder);
            OpLog.Add($"Switched to profile '{row.Name}' ({plan.Mode.ToString().ToLowerInvariant()}, {plan.NewOrder.Count} entries).");
            foreach (var m in plan.Missing) OpLog.Add($"  profile mod not installed, skipped: {m.DisplayName} ({m.Raw}).");
            Rescan();
            var msg = $"Switched to '{row.Name}'.";
            if (plan.Missing.Count > 0)
                msg += $"\n\n{plan.Missing.Count} mod(s) from the profile are no longer installed and were skipped:\n• " +
                       string.Join("\n• ", plan.Missing.Select(m => m.DisplayName));
            MessageBox.Show(this, msg, "Ostrasort", MessageBoxButton.OK,
                plan.Missing.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Ostrasort", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { _busy = false; }
    }

    private void ProfileRename_Click(object sender, RoutedEventArgs e)
    {
        if (ListProfiles.SelectedItem is not ProfileRow row) return;
        var name = PromptDialog.Ask(this, "New name for this profile:", row.Name);
        if (name is null || string.Equals(name, row.Name, StringComparison.Ordinal)) return;
        if (ProfileStore.Exists(_env.LoadingOrderPath, name))
        {
            MessageBox.Show(this, $"A profile named “{name}” already exists.", "Ostrasort",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            ProfileStore.Rename(_env.LoadingOrderPath, row.Name, name);
            OpLog.Add($"Renamed profile '{row.Name}' to '{name}'.");
            RenderProfiles();
            SelectProfile(name);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Ostrasort", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void ProfileDelete_Click(object sender, RoutedEventArgs e)
    {
        if (ListProfiles.SelectedItem is not ProfileRow row) return;
        if (MessageBox.Show(this, $"Delete profile “{row.Name}”? This removes only the saved profile, not any mods.",
                "Ostrasort", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            ProfileStore.Delete(_env.LoadingOrderPath, row.Name);
            OpLog.Add($"Deleted profile '{row.Name}'.");
            RenderProfiles();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Ostrasort", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void SelectProfile(string name)
    {
        if (ListProfiles.ItemsSource is IEnumerable<ProfileRow> rows)
            ListProfiles.SelectedItem = rows.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    // ----------------------------------------------------- window behaviour ---

    private static void OpenFolder(string? path)
    {
        if (path is null || !Directory.Exists(path)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private void OpenGame_Click(object sender, RoutedEventArgs e) => OpenFolder(_env.GameRoot);
    private void OpenMods_Click(object sender, RoutedEventArgs e) => OpenFolder(_env.ModsDir);

    private void ModsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ModsGrid.SelectedItem is ModRow { Dir: not null } row) OpenFolder(row.Dir);
    }

    /// <summary>Rescan when the window regains focus (e.g. after closing the game) - debounced and never over a manual arrangement.</summary>
    private void Window_Activated(object sender, EventArgs e)
    {
        if (_busy || _manualDirty || _state is null) return;
        if ((DateTime.Now - _lastScan).TotalSeconds < 5) return;
        Rescan();
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _settings.Maximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            _settings.Left = Left; _settings.Top = Top;
            _settings.Width = Width; _settings.Height = Height;
        }
        _settings.Tidy = ChkTidy.IsChecked == true;
        _settings.Tab = Tabs.SelectedIndex;
        _settings.Save();
    }

    private void RestoreWindowState()
    {
        if (!double.IsNaN(_settings.Left) && !double.IsNaN(_settings.Top) &&
            _settings.Left < SystemParameters.VirtualScreenWidth - 100 &&
            _settings.Top < SystemParameters.VirtualScreenHeight - 100)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = Math.Max(_settings.Left, SystemParameters.VirtualScreenLeft);
            Top = Math.Max(_settings.Top, SystemParameters.VirtualScreenTop);
        }
        Width = Math.Max(_settings.Width, MinWidth);
        Height = Math.Max(_settings.Height, MinHeight);
        if (_settings.Maximized) WindowState = WindowState.Maximized;
    }

    // ---------------------------------------------------------- update check ---

    private const string ReleasesUrl = "https://github.com/Valtora/Ostrasort/releases";
    private string _updateUrl = ReleasesUrl;

    private void CheckUpdate_Click(object sender, RoutedEventArgs e) => _ = CheckForUpdateAsync(manual: true);

    /// <summary>
    /// Compares this build against the latest GitHub release. Runs on EVERY
    /// launch (queried live each time, so a release published after this build
    /// is picked up on the next start - there is no cached "latest" to go
    /// stale) and on demand from the "Check for updates" link. A newer release
    /// surfaces the Update button, writes a Logs-tab line, and raises a modal
    /// (UpdateDialog) offering to Download Latest Version or dismiss with Not
    /// Now - shown on every launch while behind, not only on the manual check.
    /// The manual run additionally reports when you are already up to date.
    /// </summary>
    private async Task CheckForUpdateAsync(bool manual = false)
    {
        string tag, latestUrl;
        try
        {
            var json = await Http.GetStringAsync(
                "https://api.github.com/repos/Valtora/Ostrasort/releases/latest").ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            latestUrl = (doc.RootElement.TryGetProperty("html_url", out var u) ? u.GetString() : null) ?? ReleasesUrl;
        }
        catch (Exception ex)
        {
            // Marshal back to the dispatcher: the continuation can resume off the
            // UI thread, and OpLog aside, MessageBox must run on the UI thread.
            await Dispatcher.InvokeAsync(() =>
            {
                OpLog.Add($"Update check failed: {ex.Message}");
                if (manual)
                    MessageBox.Show(this, "Couldn't check for updates.\n\n" + ex.Message +
                        "\n\nYou may be offline, or GitHub may be rate-limiting — its anonymous API allows about 60 checks an hour per network.",
                        "Ostrasort", MessageBoxButton.OK, MessageBoxImage.Warning);
            });
            return;
        }

        await Dispatcher.InvokeAsync(() =>
        {
            if (ParseVersion(tag) > ParseVersion(Program.Version))
            {
                _updateUrl = latestUrl;
                BtnUpdate.Content = $"⬆  Update available: {tag}";
                BtnUpdate.Visibility = Visibility.Visible;
                OpLog.Add($"A newer release is available: {tag} (you are on v{Program.Version}).");
                // A newer release always raises the modal (every launch), not only on the manual
                // check - the toolbar button stays as the persistent affordance after "Not Now".
                // "Download Latest Version" opens the release page.
                if (UpdateDialog.Show(this, "Update available",
                        $"{tag} is available to download.\nYou're on v{Program.Version}."))
                    Process.Start(new ProcessStartInfo(_updateUrl) { UseShellExecute = true });
            }
            else
            {
                OpLog.Add($"Update check: up to date (v{Program.Version}; latest release is {tag}).");
                if (manual)
                    MessageBox.Show(this, $"You're on the latest version (v{Program.Version}).",
                        "Ostrasort", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        });
    }

    private static Version ParseVersion(string s) =>
        Version.TryParse(s.TrimStart('v', 'V').Split('+', '-')[0], out var v) ? v : new Version(0, 0);

    private void Update_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(_updateUrl) { UseShellExecute = true });

    // ---------------------------------------------------- self-install ---

    /// <summary>
    /// One-time, dismissible first-run offer to install the exe to a fixed
    /// per-user location + shortcuts. Fires on first render (never during
    /// --smoke-gui, which constructs the window without showing it) and only
    /// when running from somewhere other than the install location.
    /// </summary>
    private void Window_ContentRendered(object? sender, EventArgs e)
    {
        if (_settings.InstallPromptDismissed || !SelfInstall.CanOfferInstall()) return;
        _settings.InstallPromptDismissed = true;   // ask once, never nag - the link stays for later
        _settings.Save();
        RunInstall(alreadyInstalled: false);
    }

    private void Install_Click(object sender, RoutedEventArgs e) =>
        RunInstall(SelfInstall.IsInstalled());

    /// <summary>Shows the install prompt and performs the chosen install + shortcut creation.</summary>
    private void RunInstall(bool alreadyInstalled)
    {
        var choice = InstallDialog.Show(this, alreadyInstalled);
        if (choice is not { } c) return;
        try
        {
            var result = SelfInstall.Install(c.Desktop, c.StartMenu);
            OpLog.Add(result.Copied
                ? $"Installed Ostrasort to {result.ExePath}."
                : $"Ostrasort already installed at {result.ExePath}.");
            foreach (var s in result.Shortcuts) OpLog.Add($"Created shortcut: {s}");

            var shortcutNote = result.Shortcuts.Count > 0
                ? $" {result.Shortcuts.Count} shortcut(s) created."
                : " No shortcuts requested.";
            RunStatus.Text = (result.Copied
                ? "Installed to %LOCALAPPDATA%\\Programs\\Ostrasort."
                : "Shortcuts refreshed.") + shortcutNote + "  ";
            RunStatus.Foreground = Good;
            MessageBox.Show(this,
                (result.Copied ? $"Ostrasort was installed to:\n{result.ExePath}\n\n" : $"Using the installed copy at:\n{result.ExePath}\n\n") +
                (result.Shortcuts.Count > 0
                    ? "Shortcuts created:\n" + string.Join("\n", result.Shortcuts)
                    : "No shortcuts were created.") +
                "\n\nLaunch Ostrasort from the shortcut or that folder from now on so updates land in one place.",
                "Ostrasort", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Install failed:\n\n" + ex.Message,
                "Ostrasort", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ------------------------------------------------------ report a bug ---

    // GitHub rejects an over-long prefilled issue URL (HTTP 414), so past this
    // total-URL length we put the report on the clipboard for the user to paste.
    private const int MaxIssueUrl = 7000;

    private void ReportBug_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var prompt =
                "# Ostrasort Bug Report\n\n" +
                "## What were you trying to do?\n\n\n" +
                "## What went wrong?\n\n\n" +
                "## Exact steps to reproduce (so I can see it happen too)\n\n1. \n2. \n3. \n\n" +
                "**Screenshots**\nDrag any screenshots in here.\n\n" +
                "---\n" +
                "*Diagnostics (please keep these — they help me reproduce it):*\n" +
                $"- Ostrasort: v{Program.Version}\n" +
                $"- OS: {DescribeOs()}\n" +
                $"- Game version: {_env.InstalledVersion ?? "unknown"}\n" +
                $"- Mods registered: {_state?.Analysis.Registered.Count ?? 0}\n";

            var report = _state is not null ? MarkdownReport.Build(_env, _state, Program.Version) : null;

            // Try to embed the full report in a collapsed block; fall back to the
            // clipboard if that would make the URL too long for GitHub.
            var body = report is not null
                ? prompt + "\n<details>\n<summary>Full Ostrasort report</summary>\n\n" + report + "\n</details>\n"
                : prompt;
            var clipboardFallback = false;
            if (report is not null && IssueUrl(body).Length > MaxIssueUrl)
            {
                Clipboard.SetText(report);
                body = prompt + "\n*My full Ostrasort report is on the clipboard — pasted below:*\n\n";
                clipboardFallback = true;
            }

            Process.Start(new ProcessStartInfo(IssueUrl(body)) { UseShellExecute = true });
            OpLog.Add("Opened a pre-filled GitHub bug report in the browser" +
                      (clipboardFallback ? " (report copied to clipboard — too long to auto-fill)." :
                       report is not null ? " (full report included)." : "."));
            if (clipboardFallback)
                MessageBox.Show(this,
                    "Your mod report was too long to pre-fill automatically, so it's on your clipboard.\n\n" +
                    "In the GitHub issue that just opened, click into the description and paste it (Ctrl+V) under the diagnostics.",
                    "Ostrasort", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Ostrasort", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string IssueUrl(string body) =>
        "https://github.com/Valtora/Ostrasort/issues/new?labels=bug"
        + "&title=" + Uri.EscapeDataString("[Bug] ")
        + "&body=" + Uri.EscapeDataString(body);

    /// <summary>
    /// A human-readable OS string that distinguishes Windows 11 from 10 (which
    /// <see cref="System.Runtime.InteropServices.RuntimeInformation.OSDescription"/>
    /// does not — both report 10.0.x). Windows 11 is build 22000+; the edition
    /// ("Pro", "Home", …) comes from the registry's ProductName suffix.
    /// </summary>
    private static string DescribeOs()
    {
        var v = Environment.OSVersion.Version;
        var name = v.Major == 10 && v.Build >= 22000 ? "Windows 11" : $"Windows {v.Major}";
        string? edition = null, display = null;
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            display = key?.GetValue("DisplayVersion") as string;   // e.g. "24H2"
            if (key?.GetValue("ProductName") as string is { } product)
            {
                // ProductName is often still "Windows 10 <edition>" even on 11 — trust only the edition suffix.
                var m = System.Text.RegularExpressions.Regex.Match(product, @"Windows\s+\d+\s+(.+)$");
                if (m.Success) edition = m.Groups[1].Value.Trim();
            }
        }
        catch { /* registry unavailable — fall back to just the name/version */ }

        var s = name;
        if (edition is { Length: > 0 }) s += " " + edition;
        s += $" ({v.Major}.{v.Minor}.{v.Build}";
        s += display is { Length: > 0 } ? $", {display})" : ")";
        return s;
    }

    // ------------------------------------------- Workshop update checking ---

    /// <summary>
    /// Fills each subscribed Workshop mod's real publish time into the mod list
    /// and flags any whose published version is newer than the copy on disk.
    /// Runs after every render, but a session cache means Steam is only queried
    /// for ids we haven't seen yet, and any failure (offline, rate-limited) is
    /// silent — the folder-date fallback already rendered.
    /// </summary>
    private async Task RefreshWorkshopUpdatesAsync()
    {
        var rows = _rows;
        var toFetch = rows
            .Where(r => r.M.Kind == EntryKind.Workshop && r.M.WorkshopId is { } w
                        && long.TryParse(w, out _) && !_wsUpdated.ContainsKey(w))
            .Select(r => r.M.WorkshopId!)
            .Distinct()
            .ToList();
        if (toFetch.Count == 0) return;

        _wsCts?.Cancel();
        var cts = new System.Threading.CancellationTokenSource();
        _wsCts = cts;

        Dictionary<string, long> fetched;
        try
        {
            fetched = await FetchWorkshopUpdateTimesAsync(toFetch, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() => OpLog.Add($"Workshop update check skipped: {ex.Message}"));
            return;
        }
        if (cts.IsCancellationRequested) return;

        // The continuation can resume off the UI thread, but the mod table is an
        // ObservableCollection bound to a CollectionView — WPF only allows it to
        // be mutated (and OpLog appended) from the dispatcher thread, so marshal
        // every UI touch back explicitly rather than trusting the captured context.
        await Dispatcher.InvokeAsync(() =>
        {
            foreach (var kv in fetched) _wsUpdated[kv.Key] = kv.Value;
            if (!ReferenceEquals(rows, _rows)) return;  // a rescan replaced the table underneath us

            var updates = 0;
            for (var i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                if (r.M.Kind != EntryKind.Workshop || r.M.WorkshopId is not { } w || !fetched.ContainsKey(w)) continue;
                var (lu, upd, ub) = UpdateInfo(r.M);
                rows[i] = r with { LastUpdated = lu, UpdateText = upd, UpdateBrush = ub };
                if (upd.Length > 0) updates++;
            }
            OpLog.Add($"Workshop: checked {fetched.Count} item(s) for updates" +
                      (updates > 0 ? $"; {updates} have a newer version published." : "."));
        });
    }

    /// <summary>
    /// Batched, key-less Steam Workshop lookup of publish times. Returns a map of
    /// published-file id -> unix seconds (time_updated) for every item Steam
    /// reported successfully; ids Steam couldn't resolve are simply absent.
    /// </summary>
    private static async Task<Dictionary<string, long>> FetchWorkshopUpdateTimesAsync(
        List<string> ids, System.Threading.CancellationToken token)
    {
        var form = new List<KeyValuePair<string, string>> { new("itemcount", ids.Count.ToString()) };
        for (var i = 0; i < ids.Count; i++)
            form.Add(new($"publishedfileids[{i}]", ids[i]));

        using var content = new FormUrlEncodedContent(form);
        using var resp = await Http.PostAsync(
            "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/", content, token);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(token);

        var result = new Dictionary<string, long>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("response", out var respEl) ||
            !respEl.TryGetProperty("publishedfiledetails", out var arr) ||
            arr.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var d in arr.EnumerateArray())
        {
            if (!d.TryGetProperty("publishedfileid", out var idEl) || idEl.GetString() is not { } id) continue;
            if (d.TryGetProperty("result", out var rEl) && rEl.ValueKind == JsonValueKind.Number && rEl.GetInt32() != 1) continue;
            if (d.TryGetProperty("time_updated", out var tuEl) && tuEl.TryGetInt64(out var tu) && tu > 0)
                result[id] = tu;
        }
        return result;
    }
}
