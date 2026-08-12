using AorusLcd.Core;
using AorusLcd.Core.Nvapi;

namespace AorusLcd.Tests;

/// <summary>Verifies <see cref="RetryingI2cBus"/> rides over transient NVAPI write and read failures while surfacing real errors.</summary>
public class RetryingI2cBusTests
{
    [Fact]
    public void Write_Retries_Then_Succeeds()
    {
        var inner = new FlakyBus(writeFailuresBeforeSuccess: 3);
        var bus = new RetryingI2cBus(inner, maxAttempts: 5, retryDelayMs: 0);

        bus.Write([1, 2, 3]);

        Assert.Equal(4, inner.WriteAttempts); // 3 failures + 1 success
        Assert.Equal(new byte[] { 1, 2, 3 }, inner.LastWrite);
    }

    [Fact]
    public void Write_Rethrows_After_Exhausting_Attempts()
    {
        var inner = new FlakyBus(writeFailuresBeforeSuccess: 10);
        var bus = new RetryingI2cBus(inner, maxAttempts: 3, retryDelayMs: 0);

        Assert.Throws<NvApiException>(() => bus.Write([9]));
        Assert.Equal(3, inner.WriteAttempts);
    }

    [Fact]
    public void Write_Does_Not_Retry_NonTransient_Status()
    {
        var inner = new FlakyBus(writeFailuresBeforeSuccess: 10, failureStatus: -5);
        var bus = new RetryingI2cBus(inner, maxAttempts: 5, retryDelayMs: 0);

        var ex = Assert.Throws<NvApiException>(() => bus.Write([7]));
        Assert.Equal(-5, ex.Status);
        Assert.Equal(1, inner.WriteAttempts); // non-transient errors surface on the first attempt
    }

    [Fact]
    public void Read_Retries_Then_Succeeds()
    {
        var inner = new FlakyBus(readFailuresBeforeSuccess: 3);
        var bus = new RetryingI2cBus(inner, maxAttempts: 5, retryDelayMs: 0);

        var result = bus.Read(4);

        Assert.Equal(4, result.Length);
        Assert.Equal(4, inner.ReadAttempts); // 3 failures + 1 success
    }

    [Fact]
    public void Read_Rethrows_After_Exhausting_Attempts()
    {
        var inner = new FlakyBus(readFailuresBeforeSuccess: 10);
        var bus = new RetryingI2cBus(inner, maxAttempts: 3, retryDelayMs: 0);

        Assert.Throws<NvApiException>(() => bus.Read(4));
        Assert.Equal(3, inner.ReadAttempts);
    }

    [Fact]
    public void Read_Does_Not_Retry_NonTransient_Status()
    {
        var inner = new FlakyBus(readFailuresBeforeSuccess: 10, failureStatus: -5);
        var bus = new RetryingI2cBus(inner, maxAttempts: 5, retryDelayMs: 0);

        var ex = Assert.Throws<NvApiException>(() => bus.Read(4));
        Assert.Equal(-5, ex.Status);
        Assert.Equal(1, inner.ReadAttempts); // non-transient errors surface on the first attempt
    }

    private sealed class FlakyBus(
        int writeFailuresBeforeSuccess = 0, int readFailuresBeforeSuccess = 0, int failureStatus = -1)
        : II2cBus
    {
        public int WriteAttempts { get; private set; }
        public int ReadAttempts { get; private set; }
        public byte[]? LastWrite { get; private set; }

        public void Write(ReadOnlySpan<byte> data)
        {
            WriteAttempts++;
            if (WriteAttempts <= writeFailuresBeforeSuccess)
            {
                throw new NvApiException("write", failureStatus);
            }
            LastWrite = data.ToArray();
        }

        public byte[] Read(int count)
        {
            ReadAttempts++;
            if (ReadAttempts <= readFailuresBeforeSuccess)
            {
                throw new NvApiException("read", failureStatus);
            }
            return new byte[count];
        }

        public void Dispose()
        {
        }
    }
}
