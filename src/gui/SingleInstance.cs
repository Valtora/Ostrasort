using System.Threading;

namespace Ostrasort.Gui;

/// <summary>
/// Session-global single-instance guard. Two Ostrasort windows could otherwise
/// both write loading_order.json and race each other, so the GUI runs as one
/// instance per Windows session: the first launch owns a named mutex; a second
/// launch signals the first to come to front (via <see cref="OnActivate"/>) and
/// exits. Names are unprefixed, so they live in the caller's session/user
/// namespace (no Global\ = one per user). GUI-only: the console/headless paths
/// never touch this. (Update swaps are handled entirely by Velopack, which
/// restarts the app itself, so there is no shutdown channel here any more.)
/// </summary>
public static class SingleInstance
{
    private const string MutexName = "Ostrasort.SingleInstance";
    private const string ActivateEventName = "Ostrasort.Activate";

    private static Mutex? _mutex;
    private static EventWaitHandle? _activate;
    private static EventWaitHandle? _stop;   // private: unblocks the listener so Release() can end it
    private static Thread? _listener;

    /// <summary>Raised (off the UI thread) when another launch asks us to come to front.</summary>
    public static Action? OnActivate { get; set; }

    /// <summary>
    /// Try to become the primary instance. True = we created (own) the mutex and
    /// a background listener is now waiting for the activate signal. False =
    /// another instance already holds it (caller should signal + exit).
    /// </summary>
    public static bool TryAcquire()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            _mutex.Dispose();
            _mutex = null;
            return false;
        }

        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        _stop = new EventWaitHandle(false, EventResetMode.AutoReset);
        _listener = new Thread(Listen) { IsBackground = true, Name = "Ostrasort-SingleInstance" };
        _listener.Start();
        return true;
    }

    private static void Listen()
    {
        var handles = new WaitHandle[] { _activate!, _stop! };
        while (true)
        {
            var i = WaitHandle.WaitAny(handles);
            switch (i)
            {
                case 0: WaitForHandler(() => OnActivate)?.Invoke(); break;
                default: return;                        // _stop (Release)
            }
        }
    }

    /// <summary>
    /// A second launch can signal in the milliseconds between TryAcquire (in
    /// GuiHost, before the window exists) and MainWindow assigning the handler -
    /// wait briefly for it instead of dropping the signal into a null delegate,
    /// which would leave the second launch exited and the first un-focused.
    /// </summary>
    private static Action? WaitForHandler(Func<Action?> get)
    {
        for (var i = 0; i < 100 && get() is null; i++) Thread.Sleep(50);
        return get();
    }

    /// <summary>Ask an already-running primary to come to front. No-op if none is running.</summary>
    public static void SignalActivateExisting() => Signal(ActivateEventName);

    private static void Signal(string eventName)
    {
        try
        {
            using var ev = EventWaitHandle.OpenExisting(eventName);
            ev.Set();
        }
        catch (WaitHandleCannotBeOpenedException) { /* no primary running - nothing to signal */ }
        catch (UnauthorizedAccessException) { /* another session/user owns it - not ours to signal */ }
    }

    /// <summary>Stop the listener and release the mutex/events. Safe to call once, after the app loop ends.</summary>
    public static void Release()
    {
        try { _stop?.Set(); } catch { /* ignore */ }
        try { _listener?.Join(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
        _mutex?.Dispose();
        _activate?.Dispose();
        _stop?.Dispose();
        _mutex = null; _activate = null; _stop = null; _listener = null;
    }
}
