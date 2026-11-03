using System.Windows.Threading;

namespace Ostrasort.Gui;

/// <summary>
/// Hands work back to a window's thread from wherever an await resumed. Code
/// that starts awaiting before Application.Run (the MainWindow constructor, and
/// so the first scan it runs) has no synchronization context to return to, so
/// its continuation lands on a thread-pool thread, and the first touch of a
/// control there throws "the calling thread cannot access this object".
/// Posting through the window's Dispatcher is right whether or not the
/// dispatcher is running yet.
/// </summary>
public static class UiThread
{
    /// <summary>
    /// Runs <paramref name="ui"/> now when already on <paramref name="dispatcher"/>'s
    /// thread, otherwise queues it there and returns at once. It never waits, so it
    /// is safe before the dispatcher runs, where Dispatcher.Invoke from another
    /// thread would block until Application.Run starts it.
    /// </summary>
    public static void Post(Dispatcher dispatcher, Action ui)
    {
        if (dispatcher.CheckAccess()) ui();
        else dispatcher.InvokeAsync(ui);
    }
}

/// <summary>
/// The Saves tab's backups. Each runs on the thread pool, queued behind the one
/// before it, and its outcome (the backup, null when no save changed, or the
/// failure) goes to the caller's callback through <paramref name="toUi"/>, from
/// a pool thread every time, so the callback never depends on the thread that
/// queued it. Nothing here is async void: a failed backup reaches the callback
/// instead of being rethrown on the pool, which ends the process.
/// </summary>
public sealed class SaveBackupQueue(Func<string, SaveBackups.Backup?> create, Action<Action> toUi)
{
    /// <summary>The newest backup, running or finished (null before the first). Launch waits for it.</summary>
    public Task? Last { get; private set; }

    /// <summary>
    /// Queues a backup and returns the hand-off: a task that completes once the
    /// outcome has been passed to <c>toUi</c> (not once <paramref name="onDone"/> has run).
    /// </summary>
    public Task Enqueue(string reason, Action<SaveBackups.Backup?, Exception?> onDone)
    {
        var previous = Last;
        var backup = Task.Run(async () =>
        {
            if (previous is not null) try { await previous.ConfigureAwait(false); } catch { /* reported by its own callback */ }
            return create(reason);
        });
        Last = backup;
        return backup.ContinueWith(
            t => toUi(() => onDone(t.IsCompletedSuccessfully ? t.Result : null, t.Exception?.GetBaseException())),
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }
}
