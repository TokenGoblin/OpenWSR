using System.Drawing;
using System.Windows.Forms;

namespace OpenWSR.App;

/// <summary>
/// Surfaces threats outside the window. An in-window toast is useless when OpenWSR is
/// minimised or behind something, which is exactly when a proximity alert matters most.
/// Uses a tray icon balloon, which needs no packaging or app identity.
/// </summary>
public sealed class TrayNotifier : IDisposable
{
    private readonly NotifyIcon _icon;

    public TrayNotifier()
    {
        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Information,
            Text = "OpenWSR",
            Visible = true,
        };
        _icon.BalloonTipClicked += (_, _) => Activated?.Invoke();
    }

    /// <summary>Raised when the user clicks the balloon, so the app can come forward.</summary>
    public event Action? Activated;

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
        _icon.Dispose();
    }
}
