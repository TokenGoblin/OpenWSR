using System.Globalization;

namespace OpenWSR.Placefiles;

/// <summary>
/// Parser for the GRLevelX placefile format — the overlay format the radar community has
/// been publishing in for years, covering everything from spotter positions to warning
/// tracks. Handles both the modern statements and the original Threshold/Color/Place form.
///
/// Triangles and Image blocks are recognised and skipped rather than mis-drawn; they are
/// reported so the UI can say what it ignored.
/// </summary>
public static class PlacefileParser
{
    private static readonly char[] Comma = [','];

    public static PlacefileDocument Parse(string text)
    {
        string? title = null;
        TimeSpan? refresh = null;
        var items = new List<PlacefileItem>();
        var fonts = new List<PlacefileFont>();
        var unsupported = new List<string>();

        var color = new PlaceColor(255, 255, 255);
        double threshold = 999;
        DateTimeOffset? from = null, to = null;
        (double Lat, double Lon)? anchor = null;

        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = StripComment(lines[i]);
            if (line.Length == 0) continue;

            int colon = line.IndexOf(':');
            // "Place: lat, lon, text" — keyword is everything before the first colon,
            // but bare coordinate lines have no keyword at all.
            string keyword = colon > 0 ? line[..colon].Trim().ToLowerInvariant() : "";
            string rest = colon > 0 ? line[(colon + 1)..].Trim() : line;

            switch (keyword)
            {
                case "title":
                    title = rest;
                    break;
                case "refresh":
                    if (double.TryParse(rest, NumberStyles.Float, CultureInfo.InvariantCulture, out double minutes))
                        refresh = TimeSpan.FromMinutes(minutes);
                    break;
                case "refreshseconds":
                    if (double.TryParse(rest, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
                        refresh = TimeSpan.FromSeconds(seconds);
                    break;
                case "threshold":
                    threshold = ParseThreshold(rest);
                    break;
                case "color":
                    color = ParseColor(rest) ?? color;
                    break;
                case "timerange":
                    (from, to) = ParseTimeRange(rest);
                    break;
                case "font":
                    if (ParseFont(rest) is { } font) fonts.Add(font);
                    break;
                case "object":
                    anchor = ParsePair(rest);
                    break;
                case "end":
                    anchor = null;
                    break;
                case "place":
                {
                    var parts = rest.Split(Comma, 3);
                    if (parts.Length >= 3 && TryPair(parts[0], parts[1], out double lat, out double lon))
                        items.Add(new PlacefileLabel(lat, lon, parts[2].Trim().Trim('"'))
                        {
                            Color = color, ThresholdNm = threshold,
                            VisibleFrom = from, VisibleTo = to, Anchor = anchor,
                        });
                    break;
                }
                case "text":
                {
                    // lat, lon, fontNumber, "string" [, "hover"]
                    var parts = SplitRespectingQuotes(rest);
                    if (parts.Count >= 4 && TryPair(parts[0], parts[1], out double x, out double y))
                    {
                        int.TryParse(parts[2].Trim(), out int fontNumber);
                        items.Add(new PlacefileLabel(
                            anchor?.Lat ?? x, anchor?.Lon ?? y,
                            parts[3].Trim().Trim('"'), fontNumber,
                            anchor is null ? 0 : x, anchor is null ? 0 : y,
                            parts.Count > 4 ? parts[4].Trim().Trim('"') : null)
                        {
                            Color = color, ThresholdNm = threshold,
                            VisibleFrom = from, VisibleTo = to, Anchor = anchor,
                        });
                    }
                    break;
                }
                case "icon":
                {
                    // lat, lon, angle, fileNumber, iconNumber [, "hover"]
                    var parts = SplitRespectingQuotes(rest);
                    if (parts.Count >= 5 && TryPair(parts[0], parts[1], out double x, out double y))
                    {
                        double.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double angle);
                        int.TryParse(parts[3].Trim(), out int fileNumber);
                        int.TryParse(parts[4].Trim(), out int iconNumber);
                        items.Add(new PlacefileIcon(
                            anchor?.Lat ?? x, anchor?.Lon ?? y, angle, fileNumber, iconNumber,
                            anchor is null ? 0 : x, anchor is null ? 0 : y,
                            parts.Count > 5 ? parts[5].Trim().Trim('"') : null)
                        {
                            Color = color, ThresholdNm = threshold,
                            VisibleFrom = from, VisibleTo = to, Anchor = anchor,
                        });
                    }
                    break;
                }
                case "line":
                {
                    var parts = SplitRespectingQuotes(rest);
                    float width = parts.Count > 0 &&
                        float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float w)
                        ? w : 1f;
                    string? hover = parts.Count > 2 ? parts[2].Trim().Trim('"') : null;
                    var points = ReadPoints(lines, ref i);
                    if (points.Count >= 2)
                        items.Add(new PlacefileLine(points, width, hover)
                        {
                            Color = color, ThresholdNm = threshold,
                            VisibleFrom = from, VisibleTo = to, Anchor = anchor,
                        });
                    break;
                }
                case "polygon":
                {
                    var contours = ReadContours(lines, ref i);
                    if (contours.Count > 0)
                        items.Add(new PlacefilePolygon(contours)
                        {
                            Color = color, ThresholdNm = threshold,
                            VisibleFrom = from, VisibleTo = to, Anchor = anchor,
                        });
                    break;
                }
                case "triangles":
                case "image":
                    SkipToEnd(lines, ref i);
                    if (!unsupported.Contains(keyword)) unsupported.Add(keyword);
                    break;
                case "iconfile":
                    // Parsed for completeness; icon sheets are not downloaded yet.
                    if (!unsupported.Contains("icon images")) unsupported.Add("icon images");
                    break;
            }
        }

        return new PlacefileDocument(title, refresh, items, fonts, unsupported);
    }

    private static string StripComment(string line)
    {
        // Semicolons inside quotes are content, not comments.
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') inQuotes = !inQuotes;
            else if (line[i] == ';' && !inQuotes) return line[..i].Trim();
        }
        return line.Trim();
    }

    /// <summary>Threshold may be a plain number or carry a unit suffix.</summary>
    private static double ParseThreshold(string text)
    {
        var token = text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "999";
        return double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value : 999;
    }

    private static PlaceColor? ParseColor(string text)
    {
        var parts = text.Split([' ', ',', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return null;
        byte Channel(int index) =>
            index < parts.Length && int.TryParse(parts[index], out int v)
                ? (byte)Math.Clamp(v, 0, 255) : (byte)255;
        return new PlaceColor(Channel(0), Channel(1), Channel(2), Channel(3));
    }

    private static (DateTimeOffset?, DateTimeOffset?) ParseTimeRange(string text)
    {
        var parts = text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        DateTimeOffset? start = parts.Length > 0 && DateTimeOffset.TryParse(
            parts[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var s) ? s : null;
        DateTimeOffset? end = parts.Length > 1 && DateTimeOffset.TryParse(
            parts[1], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var e) ? e : null;
        return (start, end);
    }

    private static PlacefileFont? ParseFont(string text)
    {
        var parts = SplitRespectingQuotes(text);
        if (parts.Count < 4) return null;
        if (!int.TryParse(parts[0].Trim(), out int number)) return null;
        int.TryParse(parts[1].Trim(), out int pixels);
        int.TryParse(parts[2].Trim(), out int flags);
        return new PlacefileFont(number, pixels, flags, parts[3].Trim().Trim('"'));
    }

    private static (double Lat, double Lon)? ParsePair(string text) =>
        TryPair(text, out double lat, out double lon) ? (lat, lon) : null;

    private static bool TryPair(string text, out double a, out double b)
    {
        a = b = 0;
        var parts = text.Split(Comma, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && TryPair(parts[0], parts[1], out a, out b);
    }

    private static bool TryPair(string first, string second, out double a, out double b)
    {
        b = 0;
        return double.TryParse(first.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out a)
            && double.TryParse(second.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out b);
    }

    /// <summary>Read coordinate lines up to the matching End.</summary>
    private static List<PlacePoint> ReadPoints(string[] lines, ref int i)
    {
        var points = new List<PlacePoint>();
        while (++i < lines.Length)
        {
            var line = StripComment(lines[i]);
            if (line.Length == 0) continue;
            if (line.StartsWith("End", StringComparison.OrdinalIgnoreCase)) break;
            if (TryPair(line, out double a, out double b)) points.Add(new PlacePoint(a, b));
        }
        return points;
    }

    /// <summary>
    /// Polygon contours: a contour ends when its first point repeats, and the next point
    /// starts a new one.
    /// </summary>
    private static List<IReadOnlyList<PlacePoint>> ReadContours(string[] lines, ref int i)
    {
        var contours = new List<IReadOnlyList<PlacePoint>>();
        var current = new List<PlacePoint>();

        void Flush()
        {
            if (current.Count >= 3) contours.Add(current);
            current = [];
        }

        while (++i < lines.Length)
        {
            var line = StripComment(lines[i]);
            if (line.Length == 0) continue;
            if (line.StartsWith("End", StringComparison.OrdinalIgnoreCase)) break;
            if (!TryPair(line, out double a, out double b)) continue;

            var point = new PlacePoint(a, b);
            if (current.Count >= 3 && Close(current[0], point))
            {
                Flush();
                continue;
            }
            current.Add(point);
        }
        Flush();
        return contours;
    }

    private static bool Close(PlacePoint a, PlacePoint b) =>
        Math.Abs(a.LatDeg - b.LatDeg) < 1e-9 && Math.Abs(a.LonDeg - b.LonDeg) < 1e-9;

    private static void SkipToEnd(string[] lines, ref int i)
    {
        while (++i < lines.Length)
            if (StripComment(lines[i]).StartsWith("End", StringComparison.OrdinalIgnoreCase)) break;
    }

    /// <summary>Split on commas that sit outside double quotes.</summary>
    internal static List<string> SplitRespectingQuotes(string text)
    {
        var parts = new List<string>();
        bool inQuotes = false;
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '"') inQuotes = !inQuotes;
            else if (text[i] == ',' && !inQuotes)
            {
                parts.Add(text[start..i]);
                start = i + 1;
            }
        }
        parts.Add(text[start..]);
        return parts;
    }
}
