using AorusLcd.Core.Nvapi;

namespace AorusLcd.Core;

/// <summary>II2cBus decorator that retries transient NVAPI failures - the GPU I2C engine intermittently rejects an otherwise-valid write or read (status -1) while the bus is otherwise healthy, which would surface a successful operation as a failure (a frozen panel after an upload, or a failed status read-back after a config write). Both directions retry on status -1; every other status surfaces immediately. Panel probing runs on the raw bus, not this decorator, so presence detection still fails fast.</summary>
public sealed class RetryingI2cBus : II2cBus
{
    /// <summary>Generic NVAPI_ERROR (-1) is the only failure observed to be transient; other statuses are real and surface immediately.</summary>
    private const int TransientStatus = -1;

    private readonly II2cBus _inner;
    private readonly int _maxAttempts;
    private readonly int _retryDelayMs;

    public RetryingI2cBus(II2cBus inner, int maxAttempts = 5, int retryDelayMs = 200)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(retryDelayMs);
        _inner = inner;
        _maxAttempts = maxAttempts;
        _retryDelayMs = retryDelayMs;
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        // A ReadOnlySpan<byte> can't be captured by a delegate, so the retry loop itself can't be
        // factored into a shared helper and is duplicated in Read; only Backoff is shared.
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                _inner.Write(data);
                return;
            }
            catch (NvApiException e) when (e.Status == TransientStatus && attempt < _maxAttempts)
            {
                Backoff();
            }
        }
    }

    public byte[] Read(int count)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return _inner.Read(count);
            }
            catch (NvApiException e) when (e.Status == TransientStatus && attempt < _maxAttempts)
            {
                Backoff();
            }
        }
    }

    private void Backoff()
    {
        if (_retryDelayMs > 0)
        {
            Thread.Sleep(_retryDelayMs);
        }
    }

    public void Dispose() => _inner.Dispose();
}
