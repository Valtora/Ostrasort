using System.IO;
using System.Windows.Controls;
using System.Windows.Threading;
using Ostrasort;
using Ostrasort.Gui;
using Xunit;

namespace Ostrasort.Tests;

/// <summary>
/// The save backup's hand-back to the UI. The first scan runs in MainWindow's
/// constructor, before Application.Run, so a backup queued there has no
/// synchronization context: v0.27.0 awaited it in an async void and crashed
/// when the continuation set a control on a thread-pool thread. These run a
/// real WPF dispatcher on its own STA thread and queue the backup before that
/// dispatcher runs, the way the constructor does. No real Saves folder is read:
/// the backup itself is a stand-in.
/// </summary>
public class SaveBackupQueueTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Runs <paramref name="beforeRun"/> on a new STA thread that owns a dispatcher,
    /// then (after <paramref name="delayBeforeRun"/>) starts that dispatcher, as
    /// Application.Run does once the window's constructor returns.
    /// </summary>
    private static Dispatcher StartUiThread(Action<Dispatcher> beforeRun, TimeSpan delayBeforeRun)
    {
        var created = new TaskCompletionSource<Dispatcher>();
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            try { beforeRun(dispatcher); }
            finally { created.SetResult(dispatcher); }
            Thread.Sleep(delayBeforeRun);
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return created.Task.WaitAsync(Timeout).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task ABackupQueuedBeforeTheDispatcherRuns_IsHandledOnTheUiThread()
    {
        var outcome = new TaskCompletionSource<(bool NoContextWhenQueued, bool OnUiThread, string? Touched)>();
        var noContext = false;
        var handOff = Task.CompletedTask;
        var ui = StartUiThread(d =>
        {
            noContext = SynchronizationContext.Current is null;   // what MainWindow's constructor sees
            var control = new TextBlock();                         // stands in for the Saves tab's lists
            var queue = new SaveBackupQueue(_ => null, a => UiThread.Post(d, a));
            handOff = queue.Enqueue("game update", (_, _) =>
            {
                try
                {
                    control.Text = "rendered";   // throws off the UI thread, as RenderSaves did
                    outcome.TrySetResult((noContext, d.CheckAccess(), control.Text));
                }
                catch (InvalidOperationException) { outcome.TrySetResult((noContext, d.CheckAccess(), null)); }
            });
        }, delayBeforeRun: TimeSpan.FromMilliseconds(300));   // the backup finishes before the dispatcher starts

        try
        {
            // handing off never waits for the dispatcher (Dispatcher.Invoke would hang here)
            await handOff.WaitAsync(TimeSpan.FromMilliseconds(250));
            var (noContextWhenQueued, onUiThread, touched) = await outcome.Task.WaitAsync(Timeout);
            Assert.True(noContextWhenQueued);
            Assert.True(onUiThread);
            Assert.Equal("rendered", touched);
        }
        finally { ui.InvokeShutdown(); }
    }

    [Fact]
    public async Task AFailedBackup_ReachesTheCallbackOnTheUiThread_InsteadOfEndingTheProcess()
    {
        var outcome = new TaskCompletionSource<(Exception? Error, bool OnUiThread)>();
        var ui = StartUiThread(d =>
        {
            var queue = new SaveBackupQueue(_ => throw new IOException("the Saves folder is locked"), a => UiThread.Post(d, a));
            queue.Enqueue("game update", (b, ex) => outcome.TrySetResult((ex, d.CheckAccess())));
        }, TimeSpan.Zero);

        try
        {
            var (error, onUiThread) = await outcome.Task.WaitAsync(Timeout);
            Assert.IsType<IOException>(error);
            Assert.Equal("the Saves folder is locked", error!.Message);
            Assert.True(onUiThread);
        }
        finally { ui.InvokeShutdown(); }
    }

    [Fact]
    public async Task Backups_RunOneAtATime_InTheOrderTheyWereQueued()
    {
        var log = new List<string>();
        var queue = new SaveBackupQueue(reason =>
        {
            lock (log) log.Add($"start {reason}");
            Thread.Sleep(reason == "first" ? 150 : 0);
            lock (log) log.Add($"end {reason}");
            return null;
        }, a => a());

        var first = queue.Enqueue("first", (_, _) => { });
        var second = queue.Enqueue("second", (_, _) => { });
        await Task.WhenAll(first, second).WaitAsync(Timeout);

        Assert.Equal(new[] { "start first", "end first", "start second", "end second" }, log);
        Assert.True(queue.Last!.IsCompleted);   // what Launch waits for
    }

    [Fact]
    public async Task AFailedBackup_DoesNotStopTheNextOne()
    {
        var done = new List<(string Reason, Exception? Error)>();
        var queue = new SaveBackupQueue(reason => reason == "bad" ? throw new IOException("locked") : null, a => a());

        await queue.Enqueue("bad", (_, ex) => { lock (done) done.Add(("bad", ex)); }).WaitAsync(Timeout);
        await queue.Enqueue("good", (_, ex) => { lock (done) done.Add(("good", ex)); }).WaitAsync(Timeout);

        Assert.IsType<IOException>(done.Single(d => d.Reason == "bad").Error);
        Assert.Null(done.Single(d => d.Reason == "good").Error);
    }

    [Fact]
    public async Task Post_RunsAtOnceWhenAlreadyOnTheUiThread()
    {
        var ranInline = new TaskCompletionSource<bool>();
        var ui = StartUiThread(_ => { }, TimeSpan.Zero);
        try
        {
            await ui.InvokeAsync(() =>
            {
                var ran = false;
                UiThread.Post(ui, () => ran = true);
                ranInline.SetResult(ran);   // a click handler's call keeps running in order, not queued behind itself
            }).Task.WaitAsync(Timeout);
            Assert.True(await ranInline.Task);
        }
        finally { ui.InvokeShutdown(); }
    }
}
