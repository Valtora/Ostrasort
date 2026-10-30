using System.Windows;

namespace Ostrasort.Gui;

/// <summary>The set-once preferences, in one place. Edits a copy; the main window applies what changed on OK.</summary>
public partial class SettingsDialog : Window
{
    public string Theme => CmbTheme.SelectedIndex switch { 1 => "light", 2 => "dark", _ => "system" };
    public bool Tidy => ChkTidy.IsChecked == true;
    public bool HoldNew => ChkHoldNew.IsChecked == true;
    public bool BackupSaves => ChkBackupSaves.IsChecked == true;

    public SettingsDialog(GuiSettings s)
    {
        InitializeComponent();
        CmbTheme.SelectedIndex = s.Theme switch { "light" => 1, "dark" => 2, _ => 0 };
        ChkTidy.IsChecked = s.Tidy;
        ChkHoldNew.IsChecked = s.HoldNew;
        ChkBackupSaves.IsChecked = s.BackupSaves;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
