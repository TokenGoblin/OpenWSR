using System.IO;
using Microsoft.Win32;

namespace OpenWSR.App;

/// <summary>
/// Whether OpenWSR starts with Windows, expressed as the machine's own Run key rather than as
/// a line in settings.json.
///
/// The registry is the single source of truth here on purpose. It can be turned off from Task
/// Manager's Startup tab without this app being told, so a persisted copy of the answer would
/// be a second spelling of the same fact and the two would drift apart — the same reason
/// <see cref="AppSettings.MigrateLegacyHome"/> nulls the fields it folds in.
///
/// The entry launches with <see cref="TrayArgument"/>, so a login start goes straight to the
/// tray. Starting a full radar window over whatever someone logged in to do is not what
/// "start with Windows" means for a background watcher.
/// </summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "OpenWSR";

    /// <summary>Command-line switch meaning "start hidden, watching".</summary>
    public const string TrayArgument = "--tray";

    /// <summary>
    /// The executable out of a Run command line, which is quoted when the path has spaces in
    /// it — and the default install path does.
    /// </summary>
    private static string ExecutableIn(string command)
    {
        command = command.Trim();
        if (!command.StartsWith('"')) return command.Split(' ')[0];
        int close = command.IndexOf('"', 1);
        return close < 0 ? command[1..] : command[1..close];
    }

    /// <summary>The command the Run key should hold for this copy of the app.</summary>
    private static string? Command =>
        Environment.ProcessPath is { } exe ? $"\"{exe}\" {TrayArgument}" : null;

    /// <summary>Whether Windows is currently set to launch OpenWSR at login.</summary>
    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is not null;
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Could not read the Run key");
                return false;
            }
        }
    }

    /// <summary>
    /// Turn login start on or off. Returns whether the registry now says what was asked —
    /// a policy-locked or otherwise unwritable Run key must not leave a ticked checkbox
    /// claiming something that is not true.
    /// </summary>
    public static bool Apply(bool enabled)
    {
        if (Command is not { } command) return false;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (key is null) return false;
            if (enabled) key.SetValue(ValueName, command);
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Could not write the Run key");
            return false;
        }
    }

    /// <summary>
    /// Point an existing entry at wherever the app is now.
    ///
    /// The entry holds an absolute path, so an app that has been moved, reinstalled elsewhere
    /// or updated to a new folder keeps a Run entry that launches nothing — and it fails
    /// silently at login, which is the worst place for a silent failure. A no-op when login
    /// start is off, or when it already names this executable.
    /// </summary>
    public static void SyncPath()
    {
        if (Command is not { } command) return;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(ValueName) is not string existing) return;
            if (string.Equals(existing, command, StringComparison.OrdinalIgnoreCase)) return;

            // Only when the entry points at something that is no longer there. Repointing it
            // at whatever ran last would let a portable copy, or one `dotnet run`, silently
            // capture the login start of an installed one — and then break it for good when
            // that build directory is cleaned. A stale path is the failure this fixes; a
            // different but working path is somebody's choice.
            if (File.Exists(ExecutableIn(existing))) return;
            key.SetValue(ValueName, command);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Could not refresh the Run key");
        }
    }
}
