using System.Threading;

namespace Ostrasort.Gui;

/// <summary>
/// Session-global single-instance guard. Two Ostrasort windows could otherwise
/// both write loading_order.json and race each other, so the GUI runs as one
/// instance per Windows session: the first launch owns a named mutex; a second
/// launch signals the first to come to front (via <see cref="OnActivate"/>) and
/// exits. The same channel doubles as the updater's shutdown request - a freshly
/// downloaded build asks the running installed copy to close (<see
/// cref="OnShutdown"/>) so it can overwrite the exe. Names are unprefixed, so
/// they live in the caller's session/user namespace (no Global\ = one per user).
/// GUI-only: the console/headless paths never touch this.
/// </summary>
public static class SingleInstance
{
    private const string MutexName = "Ostrasort.SingleInstance";
    private const string ActivateEventName = "Ostrasort.Activate";
    private const string ShutdownEventName = "Ostrasort.Shutdown";

    private static Mutex? _mutex;
    private static EventWaitHandle? _activate;
    private static EventWaitHandle? _shutdown;
    private static EventWaitHandle? _stop;   // private: unblocks the listener so Release() can end it
    private static Thread? _listener;

    /// <summary>Raised (off the UI thread) when another launch asks us to come to front.</summary>
    public static Action? OnActivate { get; set; }

    /// <summary>Raised (off the UI thread) when the updater asks us to close so it can replace the exe.</summary>
    public static Action? OnShutdown { get; set; }

    /// <summary>
    /// Try to become the primary instance. True = we created (own) the mutex and
    /// a background listener is now waiting for activate/shutdown signals. False =
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
        _shutdown = new EventWaitHandle(false, EventResetMode.AutoReset, ShutdownEventName);
        _stop = new EventWaitHandle(false, EventResetMode.AutoReset);
        _listener = new Thread(Listen) { IsBackground = true, Name = "Ostrasort-SingleInstance" };
        _listener.Start();
        return true;
    }

    private static void Listen()
    {
        var handles = new WaitHandle[] { _activate!, _shutdown!, _stop! };
        while (true)
        {
            var i = WaitHandle.WaitAny(handles);
            switch (i)
            {
                case 0: OnActivate?.Invoke(); break;
                case 1: OnShutdown?.Invoke(); return;   // shutting down - stop listening
                default: return;                        // _stop (Release)
            }
        }
    }

    /// <summary>Ask an already-running primary to come to front. No-op if none is running.</summary>
    public static void SignalActivateExisting() => Signal(ActivateEventName);

    /// <summary>Ask an already-running primary to close itself (updater). No-op if none is running.</summary>
    public static void SignalShutdownExisting() => Signal(ShutdownEventName);

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
        _shutdown?.Dispose();
        _stop?.Dispose();
        _mutex = null; _activate = null; _shutdown = null; _stop = null; _listener = null;
    }
}
