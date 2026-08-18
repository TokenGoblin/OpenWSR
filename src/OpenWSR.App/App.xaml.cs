using System.IO;
using System.Windows;
using Serilog;

namespace OpenWSR.App;

public partial class App : Application
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

        DispatcherUnhandledException += (_, args) =>
            Log.Error(args.Exception, "Unhandled dispatcher exception");

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("OpenWSR exiting");
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
