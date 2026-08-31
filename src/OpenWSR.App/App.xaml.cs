using System.IO;
using System.Windows;
using Serilog;

namespace OpenWSR.App;

public partial class App : System.Windows.Application
{
    private SingleInstance? _instance;

    /// <summary>
    /// Whether this launch was told to go straight to the tray. Set by the Run-key entry
    /// (see <see cref="StartupRegistration"/>) and read by the main window before it is shown.
    /// </summary>
    public static bool StartInTray { get; private set; }

    /// <summary>
    /// Raised when a second launch asks this instance to come forward. Fires on a background
    /// thread — the window marshals it to the dispatcher.
    /// </summary>
    public static event Action? ActivationRequested;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Before the logger, deliberately. The file sink takes an exclusive lock on the day's
        // log, so a second launch that got as far as configuring Serilog would either fail
        // opening it or fight the running instance for it — and a second launch has nothing
        // to say that is worth a log line anyway. It wakes the copy that is running and goes.
        if (!SingleInstance.TryClaim(out _instance))
        {
            Shutdown();
            return;
        }
        if (_instance is not null)
            _instance.ActivationRequested += () => ActivationRequested?.Invoke();

        StartInTray = e.Args.Contains(
            StartupRegistration.TrayArgument, StringComparer.OrdinalIgnoreCase);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                Path.Combine(AppSettings.SettingsDir, "logs", "openwsr-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7)
            .CreateLogger();
        Log.Information("OpenWSR starting, version {Version}{Tray}",
            typeof(App).Assembly.GetName().Version,
            StartInTray ? " (to the tray)" : "");

        // An installed-elsewhere or moved copy leaves a login entry pointing at a path that
        // no longer exists, and it fails at login where nobody is watching. A no-op unless
        // login start is on and stale.
        StartupRegistration.SyncPath();

        // A failed fetch reaching the dispatcher used to end the session. Log it, show it,
        // and keep running — losing a loaded volume because a geocoder lookup 404'd is a
        // worse outcome than any of these exceptions.
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "Unhandled dispatcher exception");
            if (MainWindow is MainWindow window)
            {
                window.ReportError($"Something went wrong: {args.Exception.Message}");
                args.Handled = true;
            }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Fatal(args.ExceptionObject as Exception, "Unhandled domain exception");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Warning(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };

        base.OnStartup(e);

        // The window is created here rather than by StartupUri, and that is what makes the
        // single-instance guard above a guard. StartupUri navigates *after* OnStartup returns
        // and Shutdown() only queues a callback, so a second launch that has already decided
        // to leave would still run the whole MainWindow constructor on its way out — a second
        // tray icon, a second warnings fetch, a second set of timers. Exactly the duplication
        // the guard exists to prevent, arriving through the door it does not cover.
        new MainWindow().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("OpenWSR exiting");
        Log.CloseAndFlush();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
