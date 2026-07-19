using System.Windows;

namespace Ostrasort.Gui;

/// <summary>A one-line text prompt (naming / renaming a profile). Returns the trimmed value, or null on cancel/empty.</summary>
public partial class PromptDialog : Window
{
    public string Value => TxtInput.Text.Trim();

    public PromptDialog(string prompt, string initial = "")
    {
        InitializeComponent();
        TxtPrompt.Text = prompt;
        TxtInput.Text = initial;
        TxtInput.SelectAll();
        Loaded += (_, _) => TxtInput.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        // an empty name would read as Cancel to the caller - keep the dialog
        // open instead of silently doing nothing
        if (Value.Length == 0) { TxtInput.Focus(); return; }
        DialogResult = true;
    }

    public static string? Ask(Window owner, string prompt, string initial = "")
    {
        var dlg = new PromptDialog(prompt, initial) { Owner = owner };
        return dlg.ShowDialog() == true && dlg.Value.Length > 0 ? dlg.Value : null;
    }
}
