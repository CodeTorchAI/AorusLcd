using AorusLcd.Core.Nvapi;

namespace AorusLcd.Core;

/// <summary>II2cBus decorator that retries transient NVAPI write failures - the GPU I2C engine intermittently rejects an otherwise-valid write (status -1) while reads keep succeeding, which would freeze the panel until the next frame. Reads pass through unretried so panel probing still fails fast.</summary>
public sealed class RetryingI2cBus : II2cBus
{
    /// <summary>Generic NVAPI_ERROR (-1) is the only write failure observed to be transient; other statuses are real and surface immediately.</summary>
    private const int TransientWriteStatus = -1;

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
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                _inner.Write(data);
                return;
            }
            catch (NvApiException e) when (e.Status == TransientWriteStatus && attempt < _maxAttempts)
            {
                if (_retryDelayMs > 0)
                {
                    Thread.Sleep(_retryDelayMs);
                }
            }
        }
    }

    public byte[] Read(int count) => _inner.Read(count);

    public void Dispose() => _inner.Dispose();
}
