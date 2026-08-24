using OpenWSR.Geo;
using OpenWSR.Nexrad;
using OpenWSR.Render;

namespace OpenWSR.App;

/// <summary>
/// Hover inspector (value / azimuth / ranges / beam height at the cursor) and the
/// right-drag geodesic distance/bearing measuring tool.
/// </summary>
public sealed class InspectorTools
{
    private readonly MapView _mapView;
    private readonly RadarDisplayController _radar;
    private long _lastHoverTicks;

    public event Action<string>? InspectorChanged;
    public event Action<OverlayGeometry?>? MeasureChanged; // line geometry, null when done

    public InspectorTools(MapView mapView, RadarDisplayController radar)
    {
        _mapView = mapView;
        _radar = radar;
        mapView.Hovered += OnHover;
        mapView.MeasureDragged += OnMeasureDrag;
    }

    private static string Unit(Moment moment) => moment switch
    {
        Moment.Reflectivity => "dBZ",
        Moment.Velocity or Moment.SpectrumWidth => "m/s",
        Moment.DifferentialReflectivity or Moment.ClutterFilterPower => "dB",
        Moment.DifferentialPhase => "°",
        Moment.AzimuthalShear => "1/s",
        _ => "",
    };

    private void OnHover(int x, int y)
    {
        // ~20 Hz is plenty for a status line.
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if ((now - _lastHoverTicks) / (double)System.Diagnostics.Stopwatch.Frequency < 0.05)
            return;
        _lastHoverTicks = now;

        if (_radar.DisplayedSweep is not { } sweep)
            return;

        var (lat, lon) = _mapView.ScreenToLatLon(x, y);
        double groundRange = GeoMath.DistanceM(sweep.RadarLatDeg, sweep.RadarLonDeg, lat, lon);
        double azimuthRad = GeoMath.BearingRad(sweep.RadarLatDeg, sweep.RadarLonDeg, lat, lon);
        double azimuthDeg = (azimuthRad * 180.0 / Math.PI + 360.0) % 360.0;

        // Invert the 4/3-earth beam path: ground arc -> slant range and height.
        double elevation = sweep.ElevationAngleDeg * Math.PI / 180.0;
        double delta = groundRange / GeoMath.EffectiveEarthRadiusM;
        double slant = GeoMath.EffectiveEarthRadiusM * Math.Sin(delta) / Math.Cos(elevation + delta);
        var (_, height) = GeoMath.BeamPath(slant, elevation);

        string value = "—";
        int gate = (int)Math.Round((slant - sweep.FirstGateM) / sweep.GateSpacingM);
        if (gate >= 0 && gate < sweep.GateCount)
        {
            // Nearest radial by azimuth (radials are not uniformly spaced).
            int best = 0;
            double bestDiff = double.MaxValue;
            for (int i = 0; i < sweep.RadialCount; i++)
            {
                double diff = Math.Abs(((sweep.AzimuthsDeg[i] - azimuthDeg + 540.0) % 360.0) - 180.0);
                if (diff < bestDiff)
                {
                    bestDiff = diff;
                    best = i;
                }
            }
            if (bestDiff < 1.5)
            {
                float raw = sweep.Data[best * sweep.GateCount + gate];
                value = float.IsNaN(raw)
                    ? sweep.RangeFoldedMask[best * sweep.GateCount + gate] ? "RF" : "—"
                    : $"{raw:F1} {Unit(sweep.Moment)}";
            }
        }

        InspectorChanged?.Invoke(
            $"{value}   az {azimuthDeg:F1}°  slant {Units.Distance(slant / 1000)}  " +
            $"ground {Units.Distance(groundRange / 1000)}  beam {Units.Height(height)} ARL");
    }

    private void OnMeasureDrag(int startX, int startY, int x, int y, bool finished)
    {
        var (lat1, lon1) = _mapView.ScreenToLatLon(startX, startY);
        var (lat2, lon2) = _mapView.ScreenToLatLon(x, y);
        double meters = GeoMath.DistanceM(lat1, lon1, lat2, lon2); // geodesic, never Mercator
        double bearing = (GeoMath.BearingRad(lat1, lon1, lat2, lon2) * 180.0 / Math.PI + 360.0) % 360.0;

        InspectorChanged?.Invoke(
            $"Measure: {Units.DistancePrecise(meters / 1000)}  bearing {bearing:F0}°" +
            (finished ? "" : "  [release to clear]"));

        if (finished)
        {
            MeasureChanged?.Invoke(null);
            return;
        }
        var geometry = new OverlayGeometry();
        var a = GeoMath.ToMercator(lat1, lon1);
        var b = GeoMath.ToMercator(lat2, lon2);
        uint color = OverlayGeometry.Pack(255, 255, 255, 220);
        geometry.Lines.Add(new OverlayLine(a.X, a.Y, b.X, b.Y, color, 2.5f, LineCaps.Both));
        MeasureChanged?.Invoke(geometry);
    }
}
