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
                            string WorkshopId, string Notes, Brush Brush, string? Dir, string Tooltip,
                            ModEntry M, bool Draggable);
public sealed record LineVm(string Text, Brush Brush, Thickness Margin, bool Bold = false)
{
    public FontWeight Weight => Bold ? FontWeights.Bold : FontWeights.Normal;
}

public partial class MainWindow : Window
{
    private static readonly Brush Normal = Brushes.Black;
    private static readonly Brush Dim = Brushes.Gray;
    private static readonly Brush Good = Brushes.Green;
    private static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(0xB8, 0x6E, 0x00));
    private static readonly Brush Bad = Brushes.Firebrick;

    private static readonly HttpClient Http = CreateHttp();
    private static HttpClient CreateHttp()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Ostrasort");
        return c;
    }

    private readonly GameEnv _env;
    private readonly GuiSettings _settings;
    private EngineState? _state;
    private System.Collections.ObjectModel.ObservableCollection<ModRow> _rows = new();
    private bool _manualDirty;
    private bool _busy;
    private bool _ffuBannerDismissed;
    private int _attentionCount;
    private DateTime _lastScan = DateTime.MinValue;
    private Point _dragStart;
    private ModRow? _dragRow;

    // undo/redo: disk-level operation snapshots + in-memory drag arrangements
    private readonly Stack<OpSnapshot> _undo = new();
    private readonly Stack<OpSnapshot> _redo = new();
    private readonly Stack<List<string>> _arrUndo = new();
    private readonly Stack<List<string>> _arrRedo = new();

    public MainWindow(GameEnv env)
    {
        _env = env;
        _settings = GuiSettings.Load();
        InitializeComponent();
        RestoreWindowState();
        Title = $"Ostrasort v{Program.Version}";
        ChkTidy.IsChecked = _settings.Tidy;
        if (_settings.Tab >= 0 && _settings.Tab < Tabs.Items.Count) Tabs.SelectedIndex = _settings.Tab;
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
            _state = Engine.Analyze(_env, ChkTidy.IsChecked == true);
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

        RenderFfuBanner(s);
        RenderTabs(s);
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
        FfuBanner.Background = new SolidColorBrush(rival ? Color.FromRgb(0xFD, 0xEC, 0xEA) : Color.FromRgb(0xFF, 0xF8, 0xE7));
        FfuBanner.BorderBrush = rival ? Bad : new SolidColorBrush(Color.FromRgb(0xD9, 0xA4, 0x00));
        TxtFfuBannerTitle.Text = rival
            ? "OstraAutoloader active — Ostrasort is read-only on this install"
            : $"{ctx.Summary} detected — FFU ordering rules are applied";
        TxtFfuBannerTitle.Foreground = rival ? Bad : Warn;
        ListFfuBanner.ItemsSource = FfuAnalysis.Notices(s.Analysis);
        FfuBanner.Visibility = Visibility.Visible;
    }

    private void FfuBannerDismiss_Click(object sender, RoutedEventArgs e)
    {
        _ffuBannerDismissed = true;
        FfuBanner.Visibility = Visibility.Collapsed;
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
        if (!m.Registered) notes.Add("NOT REGISTERED");
        if (m.Dir is null && m.Kind != EntryKind.Core) notes.Add("DEAD ENTRY");
        if (m.IsPatch) notes.Add("generated by Ostrasort");
        if (m.HasPatchers) notes.Add("ships BepInEx patchers");
        if (m.HasPlugins && !m.HasPatchers) notes.Add("plugins");
        if (m.IsFfuPatch) notes.Add("FFU patch — remove after one use");
        else if (m.IsFfu) notes.Add(m.FfuGroup == FfuLoadGroup.FFUCore ? "FFU core tier" : "FFU mod");
        if (m.RemoveIds.Count > 0) notes.Add($"removes {m.RemoveIds.Count} core entr{(m.RemoveIds.Count == 1 ? "y" : "ies")} (FFU removeIds)");
        if (m.GameVersion is { Length: > 0 } gv && _env.InstalledVersion is { } iv && gv != iv)
            notes.Add($"gameVersion {gv} lags {iv}");
        if (m.JsonErrors.Count > 0) notes.Add($"{m.JsonErrors.Count} JSON problem(s)");
        var brush = notes.Any(n => n.StartsWith("NOT") || n.StartsWith("DEAD") || n.Contains("JSON")) ? Warn : Normal;
        var name = m.Kind == EntryKind.Core ? "core (base game data)" : m.DisplayName ?? m.Name;
        var tooltip = m.Dir ?? m.Raw;
        if (m.Kind != EntryKind.Core && m.Dir is not null) tooltip += "\n(double-click to open the folder)";
        var draggable = m.Registered && m.Kind != EntryKind.Core;
        return new ModRow(pos, name, source, cls, data, m.WorkshopId ?? "-",
            string.Join("; ", notes), brush, m.Dir, tooltip, m, draggable);
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
        BtnApply.Content = _manualDirty ? "Apply manual order" : "Apply suggested order";
        BtnPatch.IsEnabled = Patcher.HasWork(a) && !running && !rivalLock;
        BtnPatch.Content = s.Patch.Stale ? "Refresh patch (decisions kept)" : "Resolve conflicts & generate patch";
        BtnUnpatch.IsEnabled = s.Patch.Exists && !running && !rivalLock;
        BtnPatchFresh.IsEnabled = BtnPatch.IsEnabled;
        BtnPatchDelete.IsEnabled = BtnUnpatch.IsEnabled;
        BtnRestoreBak.IsEnabled = File.Exists(bak) && !running && !rivalLock;
        BtnRestoreBak.ToolTip = File.Exists(bak)
            ? $"Swap loading_order.json with the backup from {File.GetLastWriteTime(bak):yyyy-MM-dd HH:mm:ss} (restoring is reversible - it swaps)"
            : "No loading_order.json.bak exists yet";
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

            CaptureOp(_manualDirty ? "apply manual order" : "apply suggested order");
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

    private void Unpatch_Click(object sender, RoutedEventArgs e)
    {
        if (_state is null || !GateRunning()) return;
        if (MessageBox.Show(this, "Remove the generated Ostrasort Patch (folder + load-order entry)?",
                "Ostrasort", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _busy = true;
        try
        {
            CaptureOp("remove patch");
            Patcher.Remove(_env);
            OpLog.Add("Removed the Ostrasort Patch.");
            Rescan();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Ostrasort", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _busy = false; }
    }

    private void RestoreBak_Click(object sender, RoutedEventArgs e)
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
            File.WriteAllText(live, bakText);
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
        Clipboard.SetText(TextReport.Build(_env, _state, Program.Version));
        RunStatus.Text = "Report copied to the clipboard.  ";
        RunStatus.Foreground = Good;
    }

    private void SaveReport_Click(object sender, RoutedEventArgs e)
    {
        if (_state is null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = "ostrasort-report.txt",
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        File.WriteAllText(dlg.FileName, TextReport.Build(_env, _state, Program.Version));
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

    private async Task CheckForUpdateAsync()
    {
        try
        {
            var json = await Http.GetStringAsync("https://api.github.com/repos/Valtora/Ostrasort/releases/latest");
            using var doc = JsonDocument.Parse(json);
            var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            var url = doc.RootElement.TryGetProperty("html_url", out var u) ? u.GetString() : null;
            if (ParseVersion(tag) > ParseVersion(Program.Version))
            {
                LinkUpdate.Inlines.Clear();
                LinkUpdate.Inlines.Add($"{tag} available");
                LinkUpdate.NavigateUri = new Uri(url ?? "https://github.com/Valtora/Ostrasort/releases");
                TxtUpdate.Visibility = Visibility.Visible;
            }
        }
        catch { /* offline, rate-limited, or no releases yet - stay quiet */ }
    }

    private static Version ParseVersion(string s) =>
        Version.TryParse(s.TrimStart('v', 'V').Split('+', '-')[0], out var v) ? v : new Version(0, 0);

    private void Update_Click(object sender, RoutedEventArgs e)
    {
        if (LinkUpdate.NavigateUri is { } uri)
            Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
    }
}
