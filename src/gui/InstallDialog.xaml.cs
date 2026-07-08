using System.Windows;

namespace Ostrasort.Gui;

/// <summary>
/// Opt-in prompt for <see cref="SelfInstall"/>: install the exe into
/// %LOCALAPPDATA%\Programs\Ostrasort and pick which shortcuts to create.
/// </summary>
public partial class InstallDialog : Window
{
    public bool DoInstall { get; private set; }
    public bool DesktopShortcut => ChkDesktop.IsChecked == true;
    public bool StartMenuShortcut => ChkStartMenu.IsChecked == true;

    public InstallDialog(bool alreadyInstalled)
    {
        InitializeComponent();
        TxtBody.Foreground = ThemeManager.Dim;
        if (alreadyInstalled)
        {
            Title = "Ostrasort shortcuts";
            TxtHeader.Text = "Refresh your Ostrasort shortcuts?";
            TxtBody.Text = $"Ostrasort is already installed at\n{SelfInstall.InstallDir}\n\n" +
                           "Choose which shortcuts to (re)create. The installed copy is left as-is.";
            BtnInstall.Content = "Create shortcuts";
        }
        else
        {
            TxtHeader.Text = "Install Ostrasort for easy launching?";
            TxtBody.Text = "This copies Ostrasort.exe to\n" +
                           $"{SelfInstall.InstallDir}\n\n" +
                           "so you have one fixed place to keep and update it, and creates the shortcuts you pick " +
                           "below. No admin rights needed, nothing is written outside your user profile, and you " +
                           "can delete that folder any time to uninstall.";
        }
    }

    private void Install_Click(object sender, RoutedEventArgs e) { DoInstall = true; DialogResult = true; }

    /// <summary>Shows the dialog; returns the choice (or null if the user dismissed it).</summary>
    public static (bool Desktop, bool StartMenu)? Show(Window owner, bool alreadyInstalled)
    {
        var dlg = new InstallDialog(alreadyInstalled) { Owner = owner };
        dlg.ShowDialog();
        return dlg.DoInstall ? (dlg.DesktopShortcut, dlg.StartMenuShortcut) : null;
    }
}
