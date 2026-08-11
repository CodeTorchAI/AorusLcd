using AorusLcd.Core.Nvapi;

namespace AorusLcd.Tests;

/// <summary>Verifies <see cref="NvApiBusFactory"/> pins every Aorus GPU bus to port 1 at 400 kHz (the shared engine wedges otherwise). The ctor only stores fields, so no hardware is touched.</summary>
public class NvApiBusFactoryTests
{
    [Fact]
    public void Panel_UsesLcdAddressPort1At400Khz()
    {
        var bus = NvApiBusFactory.Panel(IntPtr.Zero);

        Assert.Equal(0x61, bus.Address);
        Assert.Equal(1, bus.Port);
        Assert.Equal(NvApiI2cSpeed.Khz400, bus.Speed);
    }

    [Theory]
    [InlineData((byte)0x75)]
    [InlineData((byte)0x71)]
    public void Rgb_UsesGivenAddressPort1At400Khz(byte address)
    {
        var bus = NvApiBusFactory.Rgb(IntPtr.Zero, address);

        Assert.Equal(address, bus.Address);
        Assert.Equal(1, bus.Port);
        Assert.Equal(NvApiI2cSpeed.Khz400, bus.Speed);
    }
}
