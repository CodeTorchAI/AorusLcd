using System.Runtime.Versioning;

namespace AorusLcd.Core.Nvapi;

/// <summary>Locates an NVAPI GPU/port whose 0x61 LCD controller answers EB 03, refusing buses that do not respond.</summary>
[SupportedOSPlatform("windows")]
public static class NvApiPanelLocator
{
    /// <summary>Find the first physical GPU where 0x61 answers on <paramref name="port"/>, returning its bus/name or null.</summary>
    public static (NvApiI2cBus Bus, string GpuName)? Locate(byte port = 1)
    {
        foreach (var gpu in NvApi.EnumPhysicalGpus())
        {
            var bus = new NvApiI2cBus(gpu, address: 0x61, port: port, speed: NvApiI2cSpeed.Khz400);
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
