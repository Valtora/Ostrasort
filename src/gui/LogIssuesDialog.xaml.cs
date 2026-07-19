using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Ostrasort.Gui;

/// <summary>
/// The "why is my game broken" flow: every problem the game itself reported
/// on its last launch, grouped by the mod Ostrasort attributed it to, as a
/// card with the obvious next steps (disable the mod, update it, open its
/// page). Unattributable lines get an honest summary instead of a guess.
/// Returns the mod the user chose to disable (the main window executes it
/// through its usual gates), or null.
/// </summary>
public partial class LogIssuesDialog : Window
{
    private readonly Analysis _analysis;

    /// <summary>Set when the user pressed "Disable this mod" on a card.</summary>
    public ModEntry? ToDisable { get; private set; }

    public LogIssuesDialog(Analysis analysis)
    {
        _analysis = analysis;
        InitializeComponent();

        var attributed = analysis.AllMods.Where(m => m.LogNotes.Count > 0).ToList();
        TxtIntro.Text =
            "Ostranauts reported problems the last time it ran. Ostrasort matched them to the mods below. " +
            "The classic next step: disable the suspect, launch the game, and see if the problem is gone.";
        TxtIntro.Foreground = ThemeManager.Dim;

        foreach (var m in attributed) Body.Children.Add(ModCard(m));

        // the honest un-attributed remainder (worded by LogCorrelation)
        var unattributed = analysis.Warnings.Where(w => w.Contains("not tied to a specific mod")).ToList();
        if (unattributed.Count > 0)
        {
            Body.Children.Add(new TextBlock
            {
                Text = "Not matched to a specific mod",
                FontWeight = FontWeights.SemiBold,
                FontSize = 13.5,
                Margin = new Thickness(0, 10, 0, 4),
            });
            foreach (var w in unattributed)
                Body.Children.Add(new TextBlock
                {
                    Text = "• " + w,
                    Foreground = ThemeManager.Warn,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 1, 0, 1),
                });
            Body.Children.Add(new TextBlock
            {
                Text = "The game has no per-mod marker for data loads, so matching is best-effort. " +
                       "The full lines are in the game log (link below) and the Logs tab.",
                Foreground = ThemeManager.Dim,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0),
            });
        }

        if (Body.Children.Count == 0)
            Body.Children.Add(new TextBlock
            {
                Text = "No problems were found in the last launch's logs.",
                Foreground = ThemeManager.Good,
            });
    }

    private Border ModCard(ModEntry m)
    {
        var body = new StackPanel();
        body.Children.Add(new TextBlock
        {
            Text = m.DisplayName ?? m.Name,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13.5,
        });
        foreach (var n in m.LogNotes)
            body.Children.Add(new TextBlock
            {
                Text = "• " + n,
                Foreground = ThemeManager.Bad,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0),
            });

        var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        if (m is { Registered: true, Disabled: false, Kind: not EntryKind.Core } && !m.IsPatch)
        {
            var disable = new Button
            {
                Content = "Disable this mod",
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(0, 0, 8, 0),
                ToolTip = "Keep it installed but have the game skip it, then relaunch to confirm the problem is gone. Re-enable it any time.",
            };
            disable.Click += (_, _) => { ToDisable = m; DialogResult = true; };
            buttons.Children.Add(disable);
        }
        if (m.WorkshopId is { } id)
        {
            var page = new Button
            {
                Content = "Open Workshop page",
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(0, 0, 8, 0),
                ToolTip = "Check for updates or known issues on the mod's Steam Workshop page.",
            };
            page.Click += (_, _) => OpenUrl($"https://steamcommunity.com/sharedfiles/filedetails/?id={id}");
            buttons.Children.Add(page);
        }
        if (m.Dir is { } dir)
        {
            var open = new Button { Content = "Open folder", Padding = new Thickness(12, 4, 12, 4) };
            open.Click += (_, _) =>
            {
                if (Directory.Exists(dir))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
            };
            buttons.Children.Add(open);
        }
        if (buttons.Children.Count > 0) body.Children.Add(buttons);

        return new Border
        {
            BorderBrush = ThemeManager.BannerAlarmBorder,
            Background = ThemeManager.BannerAlarmBg,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 8),
            Child = body,
        };
    }

    private void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Ostrasort", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        var path = GameEnv.PlayerLogPath;
        if (File.Exists(path))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        else
            MessageBox.Show(this, "The game log (Player.log) does not exist yet - launch the game once.",
                "Ostrasort", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
