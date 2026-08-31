namespace OpenWSR.App;

/// <summary>
/// What the tray icon says it is doing, in one line.
///
/// Split out from <see cref="TrayNotifier"/> because it is the part with rules in it and no
/// Win32 in it. The rules matter more than they look: while OpenWSR is in the tray this line
/// is the entire user interface, so it has to answer "is this thing actually watching, and is
/// anything happening" from a hover — and it has to do it inside the shell's 63-character
/// tooltip limit, which is short enough that every word has to earn its place.
/// </summary>
public static class TrayStatus
{
    /// <summary>
    /// The shell truncates a tray tooltip at 63 characters and WinForms throws above it, so
    /// this is a hard limit rather than a style guide.
    /// </summary>
    public const int TooltipLimit = 63;

    /// <summary>
    /// The menu heading: what is being watched, or what is coming, at whatever length it takes.
    ///
    /// A threat outranks the watch list, because "watching Home" is the answer to a question
    /// nobody asks while a storm is inbound. With nothing approaching it names the places, so
    /// that an app sitting silently in the tray can be told apart from one that is silent
    /// because it was never given anywhere to watch — the two look identical otherwise, and
    /// only one of them is working.
    /// </summary>
    public static string Describe(
        IReadOnlyList<WatchedPlace> places, IReadOnlyList<Threat> threats)
    {
        if (threats.Count > 0)
        {
            var worst = threats[0];
            string more = threats.Count > 1 ? $"  (+{threats.Count - 1} more)" : "";
            return $"{worst.Label} — {worst.Range}{more}";
        }

        return places.Count switch
        {
            0 => "Not watching anywhere — no place saved",
            1 => $"Watching {places[0].Name} — nothing approaching",
            var n => $"Watching {n} places — nothing approaching",
        };
    }

    /// <summary>
    /// The same line, cut to what a tray tooltip can hold, and prefixed with the app name
    /// because a tray icon has to say whose it is before it says anything else.
    /// </summary>
    public static string Tooltip(
        IReadOnlyList<WatchedPlace> places, IReadOnlyList<Threat> threats)
    {
        string text = $"OpenWSR — {Describe(places, threats)}";
        return text.Length <= TooltipLimit ? text : text[..(TooltipLimit - 1)] + "…";
    }
}
