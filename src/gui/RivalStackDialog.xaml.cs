using System.Windows;

namespace Ostrasort.Gui;

/// <summary>
/// Blocking startup gate shown when a rival, non-Workshop mod stack (Robyn's
/// OstraAutoloader / the FFU MonoMod framework) is present. Quit is the default
/// and safe choice; Continue is a deliberate at-your-own-risk override.
/// </summary>
public partial class RivalStackDialog : Window
{
    public RivalStackDialog(RivalStack rival)
    {
        InitializeComponent();

        var what = rival.Ffu
            ? "the FFU (Fight for Universe: Beyond Reach) framework"
            : rival.Autoloader ? "Robyn's OstraAutoloader"
            : "a Thunderstore / MonoMod mod stack";
        TxtBody.Text =
            $"Ostrasort has detected {what} on this install.\n\n" +
            "FFU and the OstraAutoloader bring their own load-order system — the autoloader " +
            "regenerates loading_order.json itself every time the game launches — so running " +
            "Ostrasort alongside them makes both tools fight over the same file.";
        TxtEvidence.Text = string.Join("\n", rival.Evidence.Select(e => "• " + e));
    }

    private void Continue_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Quit_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>Shows the gate modally. True = the user accepted the risk and wants to continue; false = quit.</summary>
    public static bool Confirm(RivalStack rival) => new RivalStackDialog(rival).ShowDialog() == true;
}
