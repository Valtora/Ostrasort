using System.Windows;

namespace Ostrasort.Gui;

/// <summary>
/// "A newer version is available" modal, shown on launch (and from the manual
/// update check) when a newer GitHub release exists. Two buttons: Download
/// Latest Version (opens the release page) and Not Now. Mirrors Ostraplan's
/// themed update prompt so both tools behave the same. The safe option (Not
/// Now) is focused and answers Esc, and no button is the Enter-default, so a
/// reflexive keypress never opens the browser.
/// </summary>
public partial class UpdateDialog : Window
{
    public UpdateDialog(string header, string body)
    {
        InitializeComponent();
        TxtHeader.Text = header;
        TxtBody.Text = body;
        TxtBody.Foreground = ThemeManager.Dim;
        Loaded += (_, _) => BtnNotNow.Focus();
    }

    private void Download_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    /// <summary>Returns true iff the user chose Download Latest Version (Not Now / Esc / close = false).</summary>
    public static bool Show(Window owner, string header, string body)
    {
        var dlg = new UpdateDialog(header, body) { Owner = owner };
        return dlg.ShowDialog() == true;
    }
}
