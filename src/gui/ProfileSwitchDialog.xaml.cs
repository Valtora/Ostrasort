using System.Windows;
using System.Windows.Media;

namespace Ostrasort.Gui;

/// <summary>
/// The switch preview: the current order vs. what the selected profile would
/// produce, side by side, with a Replace / Merge-append toggle (default
/// Replace) that re-renders the diff and a note about missing / dropped /
/// appended mods. Confirming exposes the computed <see cref="SwitchPlan"/> so
/// the caller writes exactly what was previewed.
/// </summary>
public partial class ProfileSwitchDialog : Window
{
    private static Brush Normal => ThemeManager.Normal;
    private static Brush Dim => ThemeManager.Dim;
    private static Brush Warn => ThemeManager.Warn;

    private readonly GameEnv _env;
    private readonly Analysis _analysis;
    private readonly Profile _profile;
    private readonly List<string> _current;

    /// <summary>Set when the user commits; carries the mode they chose.</summary>
    public SwitchPlan? Result { get; private set; }

    public ProfileSwitchDialog(GameEnv env, Analysis analysis, Profile profile)
    {
        _env = env;
        _analysis = analysis;
        _profile = profile;
        _current = analysis.Registered.Select(m => m.Raw).ToList();
        InitializeComponent();
        ThemeManager.ApplyTo(this);
        DiffBorder.BorderBrush = ThemeManager.PanelBorder;
        TxtHeader.Text = $"Switch to profile “{profile.Name}”";
        Render();
    }

    private SwitchMode Mode => RadMerge.IsChecked == true ? SwitchMode.Merge : SwitchMode.Replace;

    private void Mode_Changed(object sender, RoutedEventArgs e) => Render();

    private void Render()
    {
        if (ListDiff is null) return;   // Checked fires during InitializeComponent, before the controls exist
        var plan = ProfileSwitch.Plan(_env, _analysis, _profile, Mode);
        var rightHeader = plan.Mode == SwitchMode.Merge ? "after switch (merge)" : "after switch (replace)";
        ListDiff.ItemsSource = OrderChangeView
            .SideBySide(_analysis, _current, plan.NewOrder, "current order", rightHeader)
            .Select(v => new LineVm(v.Text, Sev(v.Sev), new Thickness(v.Indent * 18, 1, 0, 1), v.Bold)).ToList();

        var notes = new List<string>();
        if (plan.Missing.Count > 0)
            notes.Add($"⚠ {plan.Missing.Count} mod(s) in this profile are no longer installed and will be skipped: " +
                      Sample(plan.Missing.Select(m => m.DisplayName)));
        if (plan.Mode == SwitchMode.Merge && plan.Appended.Count > 0)
            notes.Add($"{plan.Appended.Count} currently-registered mod(s) not in the profile will be appended at the end.");
        if (plan.Mode == SwitchMode.Replace && plan.Dropped.Count > 0)
            notes.Add($"{plan.Dropped.Count} currently-registered mod(s) not in the profile will be removed from the order " +
                      "(their files stay on disk).");
        TxtNotes.Text = notes.Count > 0 ? string.Join("\n", notes) : "This profile matches what's installed — nothing changes.";
        TxtNotes.Foreground = plan.Missing.Count > 0 ? Warn : Dim;
    }

    private void Switch_Click(object sender, RoutedEventArgs e)
    {
        Result = ProfileSwitch.Plan(_env, _analysis, _profile, Mode);
        DialogResult = true;
    }

    private static string Sample(IEnumerable<string> names)
    {
        var list = names.ToList();
        return string.Join(", ", list.Take(6)) + (list.Count > 6 ? $", +{list.Count - 6} more" : "");
    }

    private static Brush Sev(LineSev sev) => sev switch
    {
        LineSev.Dim => Dim,
        LineSev.Warn => Warn,
        _ => Normal,
    };
}
