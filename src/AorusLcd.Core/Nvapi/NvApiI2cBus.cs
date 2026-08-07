using System.Runtime.Versioning;

namespace AorusLcd.Core.Nvapi;

/// <summary>NVAPI I2C bus for one physical GPU/port; writes raw blocks to <see cref="Address"/> on <see cref="Port"/> and disposal is a no-op.</summary>
[SupportedOSPlatform("windows")]
public sealed class NvApiI2cBus(IntPtr gpuHandle, byte address = 0x61, byte port = 1,
    NvApiI2cSpeed speed = NvApiI2cSpeed.Default) : II2cBus
{
    private const uint SpeedDeprecated = 0xFFFF;

    public byte Address { get; } = address;
    public byte Port { get; } = port;

    /// <summary>PCI bus number of the underlying GPU (for matching to NVML), or null.</summary>
    public uint? PciBusId => NvApi.GetBusId(gpuHandle);

    public void Write(ReadOnlySpan<byte> data)
    {
        unsafe
        {
            fixed (byte* ptr = data)
            {
                var info = BuildInfo((IntPtr)ptr, (uint)data.Length);
                int status = NvApi.I2CWriteEx(gpuHandle, ref info);
                if (status != 0)
                {
                    throw new NvApiException($"NvAPI_I2CWriteEx (0x{Address:X2} port {Port})", status);
                }
            }
        }
    }

    public byte[] Read(int count)
    {
        var buffer = new byte[count];
        unsafe
        {
            fixed (byte* ptr = buffer)
            {
                var info = BuildInfo((IntPtr)ptr, (uint)count);
                int status = NvApi.I2CReadEx(gpuHandle, ref info);
                if (status != 0)
                {
                    throw new NvApiException($"NvAPI_I2CReadEx (0x{Address:X2} port {Port})", status);
                }
            }
        }
        return buffer;
    }

    private NvI2cInfoV3 BuildInfo(IntPtr dataPtr, uint size) => new()
    {
        Version = NvI2cInfoV3.Version3,
        DisplayMask = 0,
        IsDdcPort = 0,
        I2cDevAddress = (byte)(Address << 1),
        I2cRegAddress = IntPtr.Zero,
        RegAddrSize = 0,
        Data = dataPtr,
        Size = size,
        I2cSpeed = SpeedDeprecated,
        I2cSpeedKhz = (uint)speed,
        PortId = Port,
        IsPortIdSet = 1,
    };

    public void Dispose()
    {
        // GPU handles are owned by NVAPI; nothing to release per-bus.
    }
}
