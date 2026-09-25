using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using OpenWSR.Geo;
using OpenWSR.Ingest;
using OpenWSR.Render;

namespace OpenWSR.App;

/// <summary>
/// Polls api.weather.gov every 60 s, renders active warning polygons above the radar in
/// NWS convention colors, and opens a detail Popup on click. Popups are separate HWNDs,
/// so they float above the D3D surface despite airspace.
/// </summary>
public sealed class WarningsController : IDisposable
{
    private readonly MapView _mapView;
    private readonly FrameworkElement _popupAnchor;
    private readonly AlertsClient _client;
    private readonly DispatcherTimer _timer;
    private IReadOnlyList<ActiveAlert> _alerts = [];
    private Popup? _openPopup;

    /// <summary>Geometry for the currently active warnings; merged by the overlay composer.</summary>
    public OverlayGeometry? Geometry { get; private set; }

    /// <summary>The active alert set (post type-filter); feeds the threat monitor.</summary>
    public IReadOnlyList<ActiveAlert> ActiveAlerts { get; private set; } = [];

    /// <summary>
    /// Every alert from the last fetch, before the layer's type filter. The dashboard draws
    /// these: unticking flood warnings tidies this map, and must not take them off a wall
    /// display in the kitchen.
    /// </summary>
    public IReadOnlyList<ActiveAlert> AllAlerts => _alerts;

    /// <summary>When the last fetch succeeded, or null before the first one has.</summary>
    public DateTimeOffset? LastRefreshUtc { get; private set; }

    // ---- layer filters; Rebuild() applies without refetching ----
    public bool ShowTornado { get; set; } = true;
    public bool ShowSevereThunderstorm { get; set; } = true;
    public bool ShowFlashFlood { get; set; } = true;
    public bool ShowOther { get; set; } = true;

    public event Action? GeometryChanged;
    public event Action<IReadOnlyList<ActiveAlert>>? AlertsUpdated;
    public event Action<string>? StatusChanged;

    /// <summary>
    /// A failure the user must see. This is the warnings layer — silently showing nothing
    /// because the poll failed is the one outcome that must never look like "no warnings".
    /// </summary>
    public event Action<string>? ErrorRaised;

    private int _consecutiveFailures;

    public WarningsController(MapView mapView, FrameworkElement popupAnchor, string userAgent)
    {
        _mapView = mapView;
        _popupAnchor = popupAnchor;
        _client = new AlertsClient(userAgent);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
        _ = RefreshAsync();
    }

    private bool PassesFilter(ActiveAlert alert) => alert.Event switch
    {
        "Tornado Warning" => ShowTornado,
        "Severe Thunderstorm Warning" => ShowSevereThunderstorm,
        "Flash Flood Warning" => ShowFlashFlood,
        _ => ShowOther,
    };

    /// <summary>Re-render the layer from the cached alert set after a filter change.</summary>
    public void Rebuild()
    {
        BuildGeometry();
        GeometryChanged?.Invoke();
        AlertsUpdated?.Invoke(ActiveAlerts);
    }

    internal static (byte R, byte G, byte B) ColorFor(string eventName) => eventName switch
    {
        "Tornado Warning" => (255, 45, 45),
        "Severe Thunderstorm Warning" => (255, 220, 40),
        "Flash Flood Warning" => (60, 220, 80),
        "Special Marine Warning" => (80, 160, 255),
        _ => (170, 170, 180),
    };

    private async Task RefreshAsync()
    {
        try
        {
            _alerts = await _client.GetActiveAsync();
            LastRefreshUtc = DateTimeOffset.UtcNow;
            _consecutiveFailures = 0;
        }
        catch (Exception ex)
        {
            // One blip is noise; a run of them means the layer is stale and the user is
            // looking at a map that claims there are no warnings.
            _consecutiveFailures++;
            StatusChanged?.Invoke($"Alerts fetch failed: {ex.Message}");
            if (_consecutiveFailures == 3)
                ErrorRaised?.Invoke(
                    "Warnings have not refreshed for several minutes — what is on the map may be "
                  + $"out of date. Last error: {ex.Message}");
            return;
        }

        BuildGeometry();
        GeometryChanged?.Invoke();
        AlertsUpdated?.Invoke(ActiveAlerts);
        StatusChanged?.Invoke($"{ActiveAlerts.Count} active warning polygon(s)");
    }

    private void BuildGeometry()
    {
        ActiveAlerts = [.. _alerts.Where(PassesFilter)];
        var geometry = new OverlayGeometry();
        foreach (var alert in ActiveAlerts)
        {
            var (r, g, b) = ColorFor(alert.Event);
            uint fill = OverlayGeometry.Pack(r, g, b, 45);
            uint stroke = OverlayGeometry.Pack(r, g, b, 235);
            foreach (var ring in alert.Polygons)
            {
                var mercator = ring
                    .Select(p => GeoMath.ToMercator(p.LatDeg, p.LonDeg))
                    .Select(m => (m.X, m.Y))
                    .ToList();
                geometry.AddPolygonFill(mercator, fill);
                geometry.AddPolygonOutline(mercator, stroke, 2.5f);
            }
        }
        Geometry = geometry;
    }

    /// <summary>Show the detail popup when the click lands in a warning polygon.</summary>
    public bool HandleClick(int x, int y)
    {
        var (lat, lon) = _mapView.ScreenToLatLon(x, y);
        var hit = ActiveAlerts.FirstOrDefault(a =>
            a.Polygons.Any(ring => GeoMath.PointInRing(lat, lon, ring)));
        _openPopup?.IsOpen = false;
        _openPopup = null;
        if (hit is null) return false;
        ShowDetailPopup(hit, x, y);
        return true;
    }

    private void ShowDetailPopup(ActiveAlert alert, int x, int y)
    {
        var (r, g, b) = ColorFor(alert.Event);
        var panel = new StackPanel { MaxWidth = 420, Margin = new Thickness(10) };
        panel.Children.Add(new TextBlock
        {
            Text = alert.Headline,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(r, g, b)),
            TextWrapping = TextWrapping.Wrap,
        });
        if (alert.Expires is { } expires)
            panel.Children.Add(new TextBlock
            {
                Text = $"Until {expires.ToLocalTime():HH:mm} local ({expires.UtcDateTime:HH:mm}Z)",
                Foreground = Brushes.LightGray,
                Margin = new Thickness(0, 4, 0, 4),
            });
        panel.Children.Add(new ScrollViewer
        {
            MaxHeight = 320,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new TextBlock
            {
                Text = alert.Description +
                       (alert.Instruction is null ? "" : $"\n\n{alert.Instruction}"),
                Foreground = Brushes.Gainsboro,
                TextWrapping = TextWrapping.Wrap,
            },
        });

        _openPopup = new Popup
        {
            PlacementTarget = _popupAnchor,
            Placement = PlacementMode.Relative,
            HorizontalOffset = x + 12,
            VerticalOffset = y + 12,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(240, 26, 28, 33)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(r, g, b)),
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(4),
                Child = panel,
            },
            IsOpen = true,
        };
    }

    public void Dispose()
    {
        _timer.Stop();
        _client.Dispose();
    }
}
