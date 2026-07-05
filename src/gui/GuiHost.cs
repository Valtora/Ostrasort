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

        // As a WinExe an unhandled exception would close the window without a
        // trace - log it (Logs tab / ostrasort.log) and tell the user instead.
        app.DispatcherUnhandledException += (_, e) =>
        {
            OpLog.Add($"[crash] Unhandled UI exception: {e.Exception}");
            MessageBox.Show(
                $"Ostrasort hit an unexpected error:\n\n{e.Exception.Message}\n\n" +
                "Details were written to the operation log (Logs tab / ostrasort.log). " +
                "Nothing is written to your game without the usual guarded ritual, but " +
                "if the window misbehaves, restart Ostrasort.",
                "Ostrasort", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            OpLog.Add($"[crash] Unhandled exception: {e.ExceptionObject}");
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            OpLog.Add($"[crash] Unobserved task exception: {e.Exception}");
            e.SetObserved();
        };

        ThemeManager.Apply(GuiSettings.Load().Theme);   // app-level, before the first window loads
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
