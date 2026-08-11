using System.Runtime.Versioning;

namespace AorusLcd.Core.Nvapi;

/// <summary>Locates an NVAPI GPU/port whose 0x61 LCD controller answers EB 03, refusing buses that do not respond.</summary>
[SupportedOSPlatform("windows")]
public static class NvApiPanelLocator
{
    private const byte DefaultPort = 1;
    private const byte LcdAddress = 0x61;

    /// <summary>Find the first physical GPU where 0x61 answers on <paramref name="port"/>, returning its bus/name or null.</summary>
    public static (NvApiI2cBus Bus, string GpuName)? Locate(byte port = DefaultPort)
    {
        foreach (var gpu in NvApi.EnumPhysicalGpus())
        {
            // Default port goes through the factory so port 1 + 400 kHz stay in
            // one place; a caller-supplied port is honored (still at 400 kHz).
            var bus = port == DefaultPort
                ? NvApiBusFactory.Panel(gpu)
                : new NvApiI2cBus(gpu, address: LcdAddress, port: port, speed: NvApiI2cSpeed.Khz400);
            if (TryProbe(bus))
            {
                return (bus, NvApi.GetFullName(gpu));
            }
        }
        return null;
    }

    private static bool TryProbe(NvApiI2cBus bus)
    {
        try
        {
            new PanelController(bus).Probe();
            return true;
        }
        catch (NvApiException)
        {
            return false;
        }
    }
}
