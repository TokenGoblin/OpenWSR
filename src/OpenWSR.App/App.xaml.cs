using System.IO;
using System.Windows;
using Serilog;

namespace OpenWSR.App;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                Path.Combine(AppSettings.SettingsDir, "logs", "openwsr-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7)
            .CreateLogger();
        Log.Information("OpenWSR starting, version {Version}",
            typeof(App).Assembly.GetName().Version);

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
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("OpenWSR exiting");
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
