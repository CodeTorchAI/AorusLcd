namespace AorusLcd.Core.Sensors;

/// <summary>
/// E3 SensorFeed keep-alive cadence. The panel firmware freezes the on-screen widgets
/// unless the feed is pushed at ~1 Hz, independent of how often the dashboard rotates
/// between widgets (rotation is handled panel-side via the E1 interval). Gigabyte's own
/// AorusLcdService pushes at a fixed 1000 ms, so we match it.
/// </summary>
public static class SensorFeedTiming
{
    /// <summary>Fixed E3 push interval in milliseconds (~1 Hz keep-alive).</summary>
    public const int KeepAlivePollMs = 1000;
}
