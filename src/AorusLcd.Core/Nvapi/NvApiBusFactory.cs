using System.Runtime.Versioning;

namespace AorusLcd.Core.Nvapi;

/// <summary>
/// The single place production code constructs an Aorus GPU I2C bus. The LCD
/// (0x61) and RGB (0x71/0x75) controllers share one physical GPU I2C engine
/// that only behaves on port 1 at 400 kHz and wedges silently at the NVAPI
/// default speed, so both invariants are enforced here rather than trusted to
/// each caller.
/// </summary>
[SupportedOSPlatform("windows")]
public static class NvApiBusFactory
{
    private const byte GpuPort = 1;
    private const byte LcdAddress = 0x61;

    /// <summary>Bus for the LCD controller at 0x61.</summary>
    public static NvApiI2cBus Panel(IntPtr gpu)
        => new(gpu, address: LcdAddress, port: GpuPort, speed: NvApiI2cSpeed.Khz400);

    /// <summary>Bus for the RGB controller at <paramref name="address"/> (0x71 legacy / 0x75 Blackwell).</summary>
    public static NvApiI2cBus Rgb(IntPtr gpu, byte address)
        => new(gpu, address: address, port: GpuPort, speed: NvApiI2cSpeed.Khz400);
}
