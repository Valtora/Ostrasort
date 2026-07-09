using System.IO;
using System.Windows;

namespace Ostrasort.Gui;

/// <summary>
/// Confirms a mod install from a .zip: lists what was found in the archive
/// (each data mod and BepInEx bundle with its destination), surfaces the
/// planner's warnings, and - when something is already installed - offers an
/// overwrite toggle. Returns the overwrite decision, or null on cancel.
/// </summary>
public partial class InstallConfirmDialog : Window
{
    private bool _overwrite;

    public InstallConfirmDialog(ModInstall.Plan plan)
    {
        InitializeComponent();

        var count = plan.Components.Count;
        TxtHeader.Text = count == 0
            ? $"Nothing to install from {Path.GetFileName(plan.ZipPath)}."
            : $"Install {count} item{(count == 1 ? "" : "s")} from {Path.GetFileName(plan.ZipPath)}?";

        ListComponents.ItemsSource = plan.Components.Select(Describe).ToList();

        var warns = plan.Warnings.ToList();
        ListWarnings.ItemsSource = warns;
        ListWarnings.Visibility = warns.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ListWarnings.Foreground = ThemeManager.Warn;

        var collisions = plan.Components.Count(c => c.Exists);
        if (collisions > 0)
        {
            ChkOverwrite.Content = collisions == 1
                ? "Overwrite the copy already installed (replaces it)"
                : $"Overwrite the {collisions} copies already installed (replaces them)";
            ChkOverwrite.Visibility = Visibility.Visible;
        }
        BtnInstall.IsEnabled = count > 0;
    }

    private static string Describe(ModInstall.Component c)
    {
        var where = c.Kind == ModInstall.ComponentKind.BepInExBundle
            ? "BepInEx mod → game BepInEx folder"
            : $"data mod → Mods\\{c.Name}";
        var tag = c.Exists ? "   (already installed)" : "";
        return $"•  {c.Name}   —   {where}{tag}";
    }

    private void Install_Click(object sender, RoutedEventArgs e)
    {
        _overwrite = ChkOverwrite.IsChecked == true;
        DialogResult = true;
    }

    /// <summary>Shows the dialog; returns the overwrite decision, or null if cancelled.</summary>
    public static bool? Show(Window owner, ModInstall.Plan plan)
    {
        var dlg = new InstallConfirmDialog(plan) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg._overwrite : null;
    }
}
