using System.Windows;

namespace Ostrasort.Gui;

/// <summary>Bootstraps WPF from the console entry point.</summary>
public static class GuiHost
{
    public static int RunMainWindow(string? gameRoot, string? modsDir, string? installName)
    {
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

        // Which install to open: an explicit --install selects (and becomes) the
        // active one; otherwise the remembered active install, else auto-detect.
        var store = InstallationStore.Load();
        if (installName is not null) store.Active = installName;
        var (env, activeName) = ResolveOrPrompt(gameRoot, modsDir, store);
        if (env is null) return 1;   // user cancelled the install picker

        app.Run(new MainWindow(env, store, activeName));
        return 0;
    }

    /// <summary>
    /// Resolve the install to open. On a Locate failure (missing/bad folders, or
    /// nothing auto-detected) open the Installations manager so the user can point
    /// Ostrasort at a real install and retry, instead of a dead-end error box.
    /// Returns (null, null) if the user gives up.
    /// </summary>
    private static (GameEnv? Env, string? Active) ResolveOrPrompt(string? cliGame, string? cliMods, InstallationStore store)
    {
        while (true)
        {
            var inst = store.Find(store.Active);
            try { return (GameEnv.Locate(cliGame ?? inst?.Game, cliMods ?? inst?.Mods), store.Active); }
            catch (Exception e)
            {
                if (new InstallationsDialog(store, e.Message).ShowDialog() != true) return (null, null);
                store.Save();
                cliGame = null; cliMods = null;   // after configuring, honor the saved install
                if (store.Find(store.Active) is null && store.Items.Count > 0)
                    store.Active = store.Items[0].Name;
            }
        }
    }

    /// <summary>Modal resolver for the console --patch path. True = decisions made, generate.</summary>
    public static bool ShowResolver(MergePlan plan)
    {
        var dlg = new ResolverDialog(plan);
        return dlg.ShowDialog() == true;
    }
}
