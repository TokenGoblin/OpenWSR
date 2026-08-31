using System.Drawing;
using System.Windows.Forms;

namespace OpenWSR.App;

/// <summary>
/// The app's presence outside its own window: the tray icon, the menu behind it, and the
/// balloons that carry a proximity alert to someone who is not looking at OpenWSR.
///
/// An in-window toast is useless when the window is minimised, behind something, or hidden in
/// the tray — which is exactly when a proximity alert matters most. A <see cref="NotifyIcon"/>
/// balloon needs no packaging and no app identity, and Windows 10 and 11 route it through the
/// same notification centre a packaged toast lands in, so it persists after it fades.
///
/// While the window is hidden this icon is the whole application as far as the user is
/// concerned, which is what the menu and the status line are for. Both are reachable with the
/// window gone, including Exit: an app that can only be quit from a window it has hidden is a
/// trap, and the taskbar button is gone by then.
/// </summary>
public sealed class TrayNotifier : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _status;
    private readonly Icon? _appIcon;
    private readonly Font? _boldMenuFont;

    public TrayNotifier()
    {
        // The exe's own icon, so the tray entry is recognisably this app rather than the
        // generic ℹ that SystemIcons offers. It is a Win32 resource of the executable, which
        // survives single-file publishing, so nothing has to be shipped beside the binary.
        try
        {
            if (Environment.ProcessPath is { } exe)
                _appIcon = Icon.ExtractAssociatedIcon(exe);
        }
        catch (Exception ex)
        {
            // A missing or unreadable icon must not stop the app appearing in the tray at
            // all — that would take the alerting with it.
            Serilog.Log.Warning(ex, "Could not read the application icon for the tray");
        }

        _status = new ToolStripMenuItem("OpenWSR") { Enabled = false };

        var menu = new ContextMenuStrip();
        var open = new ToolStripMenuItem("Open OpenWSR", null, (_, _) => Activated?.Invoke());
        // SystemFonts.MenuFont is genuinely nullable, and this constructor runs inside the
        // main window's — dereferencing it would take the whole app down over the weight of
        // one menu item. Bold is a nicety; the item works without it.
        if (SystemFonts.MenuFont is { } menuFont)
        {
            _boldMenuFont = new Font(menuFont, FontStyle.Bold);
            open.Font = _boldMenuFont;
        }
        menu.Items.Add(open);
        menu.Items.Add(_status);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(
            "Settings…", null, (_, _) => SettingsRequested?.Invoke()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(
            "Exit OpenWSR", null, (_, _) => ExitRequested?.Invoke()));

        _icon = new NotifyIcon
        {
            Icon = _appIcon ?? SystemIcons.Information,
            Text = "OpenWSR",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.BalloonTipClicked += (_, _) => Activated?.Invoke();
        // Double-click is the shell convention for "open the window"; a single left click
        // opens the menu, which right-click does too.
        _icon.DoubleClick += (_, _) => Activated?.Invoke();
    }

    /// <summary>Raised when the user asks for the window back — a balloon click, or the menu.</summary>
    public event Action? Activated;

    /// <summary>Raised for the menu's Settings item.</summary>
    public event Action? SettingsRequested;

    /// <summary>Raised for the menu's Exit item: the only way out while the window is hidden.</summary>
    public event Action? ExitRequested;

    /// <summary>
    /// What the icon says it is doing, on hover and at the top of its menu. See
    /// <see cref="TrayStatus"/> for the wording rules and the 63-character tooltip limit —
    /// WinForms throws rather than truncating, so the cap is not optional.
    /// </summary>
    public void SetStatus(
        IReadOnlyList<WatchedPlace> places, IReadOnlyList<Threat> threats)
    {
        _status.Text = TrayStatus.Describe(places, threats);
        _icon.Text = TrayStatus.Tooltip(places, threats);
    }

    public void Notify(string title, string message, bool urgent)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = message.Length > 240 ? message[..237] + "…" : message;
        _icon.BalloonTipIcon = urgent ? ToolTipIcon.Warning : ToolTipIcon.Info;
        _icon.ShowBalloonTip(urgent ? 30_000 : 12_000);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
        _appIcon?.Dispose();
        _boldMenuFont?.Dispose();
    }
}
