namespace OpenWSR.App;

/// <summary>
/// A layer that refreshes itself on a clock, and can be told to stop while nobody is looking.
///
/// Suspending is deliberately not disabling. <c>Disable</c> is the user switching a layer off:
/// it drops the data and clears the overlay. This stops the clock and keeps everything else,
/// because a window coming back from the tray has to find the map as it left it — and because
/// a layer the user never turned off must not come back turned off.
///
/// It exists for one situation: OpenWSR hidden in the notification area. Threats come from the
/// storm-track and warnings polls and nothing else, so every other timer in the app is
/// fetching and decoding for a renderer that is paused. Satellite is the case that makes the
/// point — a multiband ABI granule is about 57 MB and it refreshes every four minutes, so a
/// hidden window with the satellite layer ticked would quietly pull most of a gigabyte an
/// hour to draw nothing.
/// </summary>
public interface ITimedLayer
{
    /// <summary>
    /// Stop the clock. A no-op when this layer was not running, so resuming cannot turn on
    /// something that was off.
    /// </summary>
    void SuspendPolling();

    /// <summary>
    /// Start again, if this layer was running when it was suspended. Implementations that
    /// fetch also refresh immediately: whatever they are holding is at least as old as the
    /// pause, and the first thing someone does on coming back is look at it.
    /// </summary>
    void ResumePolling();
}
