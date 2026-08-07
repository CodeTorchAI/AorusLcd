using AorusLcd.Core.Sensors;

namespace AorusLcd.Tests;

/// <summary>Guards the E3 keep-alive cadence: the panel freezes its widgets if the feed is pushed slower than ~1 Hz.</summary>
public class SensorFeedTimingTests
{
    [Fact]
    public void KeepAlive_IsOneHz()
        => Assert.Equal(1000, SensorFeedTiming.KeepAlivePollMs);

    [Fact]
    public void KeepAlive_IsAtLeastOneHz()
    {
        // Must never drop below 1 Hz (a slower push starves the firmware keep-alive
        // and freezes the on-screen widgets), and must be a positive interval.
        Assert.InRange(SensorFeedTiming.KeepAlivePollMs, 1, 1000);
    }
}
