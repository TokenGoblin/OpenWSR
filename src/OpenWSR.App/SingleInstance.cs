using System.Runtime.InteropServices;

namespace OpenWSR.App;

/// <summary>
/// One OpenWSR per user session.
///
/// This only became necessary once the app could hide itself. A window minimised to the tray
/// has no taskbar button, so the natural way to "get it back" is to launch it again from the
/// Start menu — and without this that starts a second copy, which means two tray icons, two
/// Level II streams (a gigabyte of working set each) and, worst of all, two notifications for
/// every storm. Duplicated alerts are how a proximity alarm stops being believed.
///
/// So a second launch does what the user meant: it wakes the copy that is already running and
/// exits. The handshake is a named mutex to decide who is first and a named event to carry the
/// "come back" — both cheap, both cleaned up by the OS if a process dies holding them, and
/// neither needing a pipe, a socket or a window message pump.
///
/// The names are <c>Local\</c>-scoped, so two people logged in to the same machine each get
/// their own instance, which is right: they have their own settings and their own places.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\OpenWSR.Instance";
    private const string EventName = @"Local\OpenWSR.Activate";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private readonly EventWaitHandle _stop = new(false, EventResetMode.ManualReset);
    private readonly Thread _listener;

    private SingleInstance(Mutex mutex, EventWaitHandle activate)
    {
        _mutex = mutex;
        _activate = activate;
        _listener = new Thread(Listen)
        {
            Name = "OpenWSR.SingleInstance",
            IsBackground = true,
        };
        _listener.Start();
    }

    /// <summary>
    /// Raised on a background thread when another launch asks for this instance. Marshal to
    /// the dispatcher before touching anything.
    /// </summary>
    public event Action? ActivationRequested;

    /// <summary>
    /// Claim the slot.
    ///
    /// Returns false only when another instance already holds it — that one has been asked to
    /// come forward, and this process should exit without showing anything. Everything else,
    /// the guard itself failing included, returns true: a machine whose policy blocks named
    /// kernel objects should get a running radar viewer, not a dialog about mutexes. The
    /// instance is null in that case and the app simply runs unguarded.
    /// </summary>
    public static bool TryClaim(out SingleInstance? instance)
    {
        instance = null;
        Mutex? mutex = null;
        EventWaitHandle? activate = null;
        try
        {
            // The event first, deliberately. Creating the mutex first leaves a window between
            // claiming it and publishing the event in which a second launch sees the slot
            // taken, finds no event to signal, and exits without waking anybody — a
            // double-click that does nothing at all. Two copies starting together at login is
            // exactly when that happens. This constructor creates or opens, so both sides end
            // up holding the same event whichever order they arrive in.
            activate = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
            mutex = new Mutex(initiallyOwned: true, MutexName, out bool first);
            if (first)
            {
                instance = new SingleInstance(mutex, activate);
                mutex = null;    // owned by the instance now
                activate = null;
                return true;
            }

            // Hand our foreground rights over before leaving. Windows' foreground lock would
            // otherwise refuse the running instance's Activate(), so asking for a window you
            // cannot see would flash a taskbar button instead of raising it — and with the
            // window hidden in the tray there is no taskbar button to flash.
            AllowSetForegroundWindow(AsfwAny);
            activate.Set();
            return false;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Single-instance check failed; starting anyway");
            mutex?.Dispose();
            activate?.Dispose();
            return true;
        }
    }

    /// <summary>Any process may take the foreground from us — we are about to exit.</summary>
    private const int AsfwAny = -1;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    private void Listen()
    {
        WaitHandle[] handles = [_activate, _stop];
        while (WaitHandle.WaitAny(handles) == 0)
            ActivationRequested?.Invoke();
    }

    public void Dispose()
    {
        _stop.Set();
        _listener.Join(TimeSpan.FromSeconds(1));
        try { _mutex.ReleaseMutex(); } catch (ApplicationException) { /* never owned it */ }
        _mutex.Dispose();
        _activate.Dispose();
        _stop.Dispose();
    }
}
