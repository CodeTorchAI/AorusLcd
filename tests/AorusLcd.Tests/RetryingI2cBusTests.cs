using AorusLcd.Core;
using AorusLcd.Core.Nvapi;

namespace AorusLcd.Tests;

/// <summary>Verifies <see cref="RetryingI2cBus"/> rides over transient NVAPI write failures while leaving reads unretried.</summary>
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
    public void Read_Passes_Through_Without_Retry()
    {
        var inner = new FlakyBus(writeFailuresBeforeSuccess: 0);
        var bus = new RetryingI2cBus(inner, maxAttempts: 5, retryDelayMs: 0);

        Assert.Throws<NvApiException>(() => bus.Read(4));
        Assert.Equal(1, inner.ReadAttempts);
    }

    private sealed class FlakyBus(int writeFailuresBeforeSuccess) : II2cBus
    {
        public int WriteAttempts { get; private set; }
        public int ReadAttempts { get; private set; }
        public byte[]? LastWrite { get; private set; }

        public void Write(ReadOnlySpan<byte> data)
        {
            WriteAttempts++;
            if (WriteAttempts <= writeFailuresBeforeSuccess)
            {
                throw new NvApiException("write", -1);
            }
            LastWrite = data.ToArray();
        }

        public byte[] Read(int count)
        {
            ReadAttempts++;
            throw new NvApiException("read", -1);
        }

        public void Dispose()
        {
        }
    }
}
