namespace AorusLcd.Core;

/// <summary>
/// Coalesces rapid config-change signals into at most one reload per debounce window.
/// The config file is machine-wide and writable by unelevated users, so a process could
/// rewrite it in a tight loop; this bounds how often the service re-probes the bus while
/// still letting the last written state win, because each reload reads the file fresh.
/// </summary>
public sealed class ReloadThrottle
{
    private readonly long _debounceMs;
    private readonly Func<long> _nowMs;
    private long _lastReloadMs;

    /// <param name="debounceMs">Minimum spacing between successive reloads.</param>
    /// <param name="nowMs">Monotonic millisecond clock; defaults to <see cref="Environment.TickCount64"/>.</param>
    public ReloadThrottle(int debounceMs, Func<long>? nowMs = null)
    {
        _debounceMs = debounceMs;
        _nowMs = nowMs ?? (() => Environment.TickCount64);
        // Start a window in the past so the first reload runs immediately.
        _lastReloadMs = _nowMs() - _debounceMs;
    }

    /// <summary>Milliseconds to wait before the next reload may run; 0 when the debounce window has elapsed.</summary>
    public int DelayUntilNextReload()
    {
        long elapsed = _nowMs() - _lastReloadMs;
        if (elapsed >= _debounceMs)
        {
            return 0;
        }
        return (int)(_debounceMs - elapsed);
    }

    /// <summary>Record that a reload just happened, opening a fresh debounce window.</summary>
    public void MarkReloaded() => _lastReloadMs = _nowMs();
}
