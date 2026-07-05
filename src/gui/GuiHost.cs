using System.Windows;

namespace Ostrasort.Gui;

/// <summary>Bootstraps WPF from the console entry point.</summary>
public static class GuiHost
{
    public static int RunMainWindow(string? gameRoot)
    {
        GameEnv env;
        try { env = GameEnv.Locate(gameRoot); }
        catch (Exception e)
        {
            MessageBox.Show(e.Message, "Ostrasort", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
        // FFU installs open normally: the main window shows a banner and, while
        // the OstraAutoloader is active, disables every write (analysis-only).
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.Run(new MainWindow(env));
        return 0;
    }

    /// <summary>Modal resolver for the console --patch path. True = decisions made, generate.</summary>
    public static bool ShowResolver(MergePlan plan)
    {
        var dlg = new ResolverDialog(plan);
        return dlg.ShowDialog() == true;
    }
}
