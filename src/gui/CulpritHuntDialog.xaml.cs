using System.Windows;

namespace Ostrasort.Gui;

/// <summary>
/// One screen of the culprit hunt: the offer to start, the current round with
/// its two answers, or the result. It only reports the player's choice; the
/// main window does the writing (see <see cref="CulpritHunt"/>).
/// </summary>
public partial class CulpritHuntDialog : Window
{
    public enum Action { None, Start, Launch, StillBroken, Gone, RestoreKeepOff, Restore }

    public Action Choice { get; private set; }

    public CulpritHuntDialog(CulpritHunt? hunt, Analysis a)
    {
        InitializeComponent();
        foreach (var b in new[] { BtnStart, BtnLaunch, BtnBroken, BtnGone, BtnKeepOff, BtnRestore, BtnStop })
            b.Visibility = Visibility.Collapsed;

        string NameOf(string key) =>
            a.AllMods.FirstOrDefault(m => string.Equals(CulpritHunt.Key(m.Raw), key, StringComparison.OrdinalIgnoreCase)) is { } m
                ? m.DisplayName ?? m.Name : key;

        if (hunt is null)
        {
            var count = CulpritHunt.Candidates(a).Count;
            TxtTitle.Text = "Find the mod behind a problem";
            if (count < 2)
            {
                TxtBody.Text = "A hunt needs at least two active mods to compare. Turn the mod off and launch the game to test it directly.";
            }
            else
            {
                var rounds = (int)Math.Ceiling(Math.Log2(count));
                TxtBody.Text =
                    $"Ostrasort turns off about half of your {count} active mods, you launch the game and check whether the problem " +
                    $"still happens, and the suspects halve each time. That takes about {rounds} launches. Mods that depend on a turned-off " +
                    "mod go off with it, the mod loader stays on, and the compatibility patch stays off until the hunt ends.";
                TxtList.Text = "Your saves are backed up first. Playing with mods turned off can still damage a save, so load one you can " +
                               "lose, or start a new game, while hunting. Mods you install during the hunt are not kept when it ends.";
                BtnStart.Visibility = Visibility.Visible;
            }
            return;
        }

        if (hunt.Finished)
        {
            TxtTitle.Text = "Hunt finished";
            if (hunt.Inseparable)
            {
                TxtBody.Text = $"These {hunt.Suspects.Count} mods can only be turned off together, because they depend on each other, " +
                               "so the hunt cannot tell them apart. One of them is the likely cause:";
                TxtList.Text = string.Join("\n", hunt.Suspects.Select(NameOf));
            }
            else if (hunt.Suspects.Count == 1)
            {
                TxtBody.Text = $"The likely culprit is {NameOf(hunt.Suspects[0])}. Look for an update to it, or report the problem to its author.";
                BtnKeepOff.Content = $"Restore my mods, keep {NameOf(hunt.Suspects[0])} off";
                BtnKeepOff.Visibility = Visibility.Visible;
            }
            else
            {
                TxtBody.Text = "The answers rule out every mod. The problem may be in the base game, in the mod loader, or caused by two " +
                               "mods only together. Check the game's log on the Logs tab for clues.";
            }
            BtnRestore.Visibility = Visibility.Visible;
            return;
        }

        TxtTitle.Text = $"Round {hunt.Round}: {hunt.Suspects.Count} suspects left";
        TxtBody.Text = $"These {hunt.Off.Count} mod(s) are turned off for this round. Launch the game, try to make the problem happen, " +
                       "then close the game and say what happened.";
        TxtList.Text = string.Join("\n", hunt.Off.Select(NameOf));
        var logged = a.AllMods.Where(m => m.LogNotes.Count > 0 && hunt.Suspects.Contains(CulpritHunt.Key(m.Raw), StringComparer.OrdinalIgnoreCase))
            .Select(m => m.DisplayName ?? m.Name).ToList();
        if (logged.Count > 0)
            TxtHint.Text = $"Hint: at the last launch the game's log reported problems from {string.Join(", ", logged)}.";
        BtnLaunch.Visibility = BtnBroken.Visibility = BtnGone.Visibility = BtnStop.Visibility = Visibility.Visible;
    }

    private void Done(Action a) { Choice = a; DialogResult = true; }
    private void Start_Click(object sender, RoutedEventArgs e) => Done(Action.Start);
    private void Launch_Click(object sender, RoutedEventArgs e) => Done(Action.Launch);
    private void Broken_Click(object sender, RoutedEventArgs e) => Done(Action.StillBroken);
    private void Gone_Click(object sender, RoutedEventArgs e) => Done(Action.Gone);
    private void KeepOff_Click(object sender, RoutedEventArgs e) => Done(Action.RestoreKeepOff);
    private void Restore_Click(object sender, RoutedEventArgs e) => Done(Action.Restore);
}
