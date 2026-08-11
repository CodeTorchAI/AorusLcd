using AorusLcd.Core;

namespace AorusLcd.Tests;

/// <summary>Verifies <see cref="ReloadThrottle"/> coalesces rapid config-change signals into at most one reload per debounce window while still letting the latest state through.</summary>
public class ReloadThrottleTests
{
    [Fact]
    public void First_Reload_Runs_Immediately()
    {
        long now = 10_000;
        var throttle = new ReloadThrottle(1000, () => now);

        Assert.Equal(0, throttle.DelayUntilNextReload());
    }

    [Fact]
    public void Reload_Within_Window_Is_Delayed_By_Remaining_Time()
    {
        long now = 10_000;
        var throttle = new ReloadThrottle(1000, () => now);

        throttle.MarkReloaded();
        now += 300;

        Assert.Equal(700, throttle.DelayUntilNextReload());
    }

    [Fact]
    public void Reload_After_Window_Runs_Immediately()
    {
        long now = 10_000;
        var throttle = new ReloadThrottle(1000, () => now);

        throttle.MarkReloaded();
        now += 1000;

        Assert.Equal(0, throttle.DelayUntilNextReload());
    }

    [Fact]
    public void Burst_Of_Signals_Collapses_To_One_Reload_Per_Window()
    {
        long now = 10_000;
        var throttle = new ReloadThrottle(1000, () => now);

        // First reload runs, opening a window.
        Assert.Equal(0, throttle.DelayUntilNextReload());
        throttle.MarkReloaded();

        // A burst arriving 100ms apart is held off, not dropped: each still owes the
        // remaining window, so a single delayed reload will later pick up the last state.
        for (int i = 0; i < 5; i++)
        {
            now += 100;
            Assert.True(throttle.DelayUntilNextReload() > 0);
        }

        // Once the window elapses the coalesced reload is released.
        now += 500;
        Assert.Equal(0, throttle.DelayUntilNextReload());
    }
}
