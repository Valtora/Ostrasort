using System.Windows;

namespace Ostrasort.Gui;

/// <summary>The guided culprit hunt: turning halves of the mod list off until one mod is left (see <see cref="CulpritHunt"/>).</summary>
public partial class MainWindow
{
    private CulpritHunt? _hunt;

    private void Hunt_Click(object sender, RoutedEventArgs e) => OpenHunt();

    private void OpenHunt()
    {
        if (_state is null) return;
        _hunt = CulpritHunt.Load(_env.LoadingOrderPath);
        var dlg = new CulpritHuntDialog(_hunt, _state.Analysis) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        switch (dlg.Choice)
        {
            case CulpritHuntDialog.Action.Launch:
                Launch_Click(this, new RoutedEventArgs());
                return;
            case CulpritHuntDialog.Action.Start:
                if (!GateRunning() || !GateRival() || !ConfirmDiscardArrangement()) return;
                if (CulpritHunt.Start(_state.Analysis, _env.LoadingOrderPath, DateTime.Now) is not { } hunt) return;
                BackupSaves("culprit hunt");
                if (!WriteHuntRound(hunt, "start a culprit hunt")) return;
                OpLog.Add($"Started a culprit hunt over {hunt.Suspects.Count} mods.");
                break;
            case CulpritHuntDialog.Action.StillBroken or CulpritHuntDialog.Action.Gone:
                if (_hunt is null || !GateRunning() || !GateRival()) return;
                var broken = dlg.Choice == CulpritHuntDialog.Action.StillBroken;
                _hunt.Answer(broken);
                OpLog.Add($"Culprit hunt round {_hunt.Round}: problem {(broken ? "still happens" : "gone")}, {_hunt.Suspects.Count} suspect(s) left.");
                if (!WriteHuntRound(_hunt, $"culprit hunt round {_hunt.Round + 1}")) return;
                break;
            case CulpritHuntDialog.Action.Restore or CulpritHuntDialog.Action.RestoreKeepOff:
                if (_hunt is null || !GateRunning() || !GateRival()) return;
                EndHunt(_hunt, keepCulpritOff: dlg.Choice == CulpritHuntDialog.Action.RestoreKeepOff);
                return;
        }
        OpenHunt();   // show the next round, or the result
    }

    /// <summary>Plans the next round and writes its order; a finished hunt writes nothing and just saves its state.</summary>
    private bool WriteHuntRound(CulpritHunt hunt, string label)
    {
        if (_state is null) return false;
        _busy = true;
        try
        {
            var order = hunt.NextRound(_state.Analysis);
            if (!hunt.Finished)
            {
                CaptureOp(label);
                LoadOrderFile.Read(_env.LoadingOrderPath).Write(order);
                OpLog.Add($"Culprit hunt round {hunt.Round}: turned off {hunt.Off.Count} mod(s).");
            }
            hunt.Save();
            Rescan();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Ostrasort", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        finally { _busy = false; }
    }

    private void EndHunt(CulpritHunt hunt, bool keepCulpritOff)
    {
        _busy = true;
        try
        {
            CaptureOp("end the culprit hunt");
            LoadOrderFile.Read(_env.LoadingOrderPath).Write(hunt.FinalOrder(keepCulpritOff));
            CulpritHunt.Clear();
            _hunt = null;
            OpLog.Add(keepCulpritOff ? $"Ended the culprit hunt; restored the load order with {hunt.Suspects[0]} kept off."
                                     : "Ended the culprit hunt; restored the load order.");
            Rescan();
            RunStatus.Text = "Your mods are back as they were before the hunt.  ";
            RunStatus.Foreground = Good;
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Ostrasort", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { _busy = false; }
    }

    /// <summary>The health card while a hunt runs: what is happening and what to press, instead of the usual verdict.</summary>
    private bool RenderHuntHealth()
    {
        if (_hunt is null) return false;
        HealthBanner.Background = ThemeManager.BannerInfoBg;
        HealthBanner.BorderBrush = ThemeManager.BannerInfoBorder;
        TxtHealthTitle.Foreground = Warn;
        TxtHealthDetail.Foreground = Normal;
        if (_hunt.Finished)
        {
            TxtHealthTitle.Text = "The culprit hunt has finished.";
            TxtHealthDetail.Text = "Press Find a problem mod… to see the result and put your mods back.";
        }
        else
        {
            TxtHealthTitle.Text = $"A culprit hunt is running (round {_hunt.Round}, {_hunt.Suspects.Count} suspects left).";
            TxtHealthDetail.Text = $"{_hunt.Off.Count} mod(s) are turned off for this round. Launch the game and check for the problem, " +
                                   "then press Find a problem mod… to say what happened.";
        }
        BtnHealthFix.Visibility = Visibility.Collapsed;
        BtnHealthLog.Visibility = Visibility.Collapsed;
        BtnHealthShow.Visibility = Visibility.Collapsed;
        LnkHealthDismiss.Visibility = Visibility.Collapsed;
        HealthBanner.Visibility = Visibility.Visible;
        return true;
    }
}
