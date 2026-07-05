using System.Windows;

namespace Ostrasort.Gui;

public enum ParkOrDelete { Cancel, Park, Delete }

/// <summary>
/// Small confirm for the FFU / autoloader removal actions. The full rationale
/// lives in the banner; this only asks HOW: park as *.disabled (reversible)
/// or delete the files outright.
/// </summary>
public partial class ParkOrDeleteDialog : Window
{
    public ParkOrDelete Choice { get; private set; } = ParkOrDelete.Cancel;

    public ParkOrDeleteDialog(string header, string body)
    {
        InitializeComponent();
        ThemeManager.ApplyTo(this);
        TxtHeader.Text = header;
        TxtBody.Text = body;
        TxtBody.Foreground = ThemeManager.Dim;
    }

    private void Park_Click(object sender, RoutedEventArgs e) { Choice = ParkOrDelete.Park; DialogResult = true; }
    private void Delete_Click(object sender, RoutedEventArgs e) { Choice = ParkOrDelete.Delete; DialogResult = true; }

    public static ParkOrDelete Show(Window owner, string header, string body)
    {
        var dlg = new ParkOrDeleteDialog(header, body) { Owner = owner };
        dlg.ShowDialog();
        return dlg.Choice;
    }
}
