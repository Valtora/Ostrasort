using System.Windows;

namespace Ostrasort.Gui;

/// <summary>A one-line text prompt (naming / renaming a profile). Returns the trimmed value, or null on cancel/empty.</summary>
public partial class PromptDialog : Window
{
    public string Value => TxtInput.Text.Trim();

    public PromptDialog(string prompt, string initial = "")
    {
        InitializeComponent();
        ThemeManager.ApplyTo(this);
        TxtPrompt.Text = prompt;
        TxtInput.Text = initial;
        TxtInput.SelectAll();
        Loaded += (_, _) => TxtInput.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    public static string? Ask(Window owner, string prompt, string initial = "")
    {
        var dlg = new PromptDialog(prompt, initial) { Owner = owner };
        return dlg.ShowDialog() == true && dlg.Value.Length > 0 ? dlg.Value : null;
    }
}
