using System.Runtime.Versioning;
using AorusLcd.Core.Rgb;

namespace AorusLcd.Core.Nvapi;

/// <summary>Which GPU RGB Fusion 2 protocol a card speaks: legacy 8-byte (pre-Blackwell) or 64-byte Blackwell (RTX 50-series).</summary>
public enum RgbControllerKind
{
    Legacy,
    Blackwell,
}

/// <summary>Locates the Aorus GPU RGB controller on the verified Aorus card (LCD answers 0x61) at a documented address (0x71/0x75).</summary>
[SupportedOSPlatform("windows")]
public static class RgbLocator
{
    private const byte BlackwellAddress = 0x75;
    private const byte LegacyAddress = 0x71;

    /// <summary>Classify a GPU by name: RTX 50-series desktop (5050-5090) uses the Blackwell protocol; everything else the legacy one.</summary>
    public static RgbControllerKind ClassifyByName(string? gpuName)
    {
        if (string.IsNullOrEmpty(gpuName))
        {
            return RgbControllerKind.Legacy;
        }
        // Match "RTX 50X0" with X in 5..9 (5050/5060/5070/5080/5090) so the
        // workstation "RTX 5000" (Ada/Turing) is not misread as 50-series.
        var n = gpuName.ToUpperInvariant().Replace(" ", "");
        int i = n.IndexOf("RTX50", StringComparison.Ordinal);
        if (i >= 0)
        {
            int tens = i + 5;
            if (tens + 1 < n.Length && n[tens] is >= '5' and <= '9' && n[tens + 1] == '0')
            {
                return RgbControllerKind.Blackwell;
            }
        }
        return RgbControllerKind.Legacy;
    }

    /// <summary>
    /// Find the RGB controller bus on the verified Aorus GPU. Each address is
    /// probed with its own protocol (0x75 = Blackwell, 0x71 = legacy) so a
    /// 64-byte Blackwell probe is never sent to the legacy address; the GPU name
    /// only decides which to try first. Self-correcting - it returns whatever
    /// protocol the hardware actually answers - or null if nothing responds.
    /// </summary>
    public static (RgbControllerKind Kind, NvApiI2cBus Bus, string GpuName, byte Address)? LocateBus()
    {
        foreach (var gpu in AorusGpus())
        {
            string name = NvApi.GetFullName(gpu);
            foreach (var (addr, kind) in CandidateOrder(ClassifyByName(name)))
            {
                // Pin to the same 400 kHz the LCD path uses: RGB shares the one
                // physical GPU I2C engine, and default-speed writes wedge it,
                // freezing/blanking the panel (the recurring 0x61 freeze).
                var bus = NvApiBusFactory.Rgb(gpu, addr);
                if (Present(bus, kind))
                {
                    return (kind, bus, name, addr);
                }
                bus.Dispose();
            }
        }
        return null;
    }

    // (address, protocol) pairs to probe, ordered by the name-classified generation.
    private static (byte Address, RgbControllerKind Kind)[] CandidateOrder(RgbControllerKind classified)
        => classified == RgbControllerKind.Blackwell
            ? [(BlackwellAddress, RgbControllerKind.Blackwell), (LegacyAddress, RgbControllerKind.Legacy)]
            : [(LegacyAddress, RgbControllerKind.Legacy), (BlackwellAddress, RgbControllerKind.Blackwell)];

    private static bool Present(NvApiI2cBus bus, RgbControllerKind kind)
        => kind == RgbControllerKind.Blackwell
            ? new RgbFusion2BlackwellController(bus).Detect()
            : new RgbFusion2Controller(bus).Detect();

    /// <summary>GPUs confirmed as the Aorus LCD card (0x61 answers), gating RGB writes to the correct card.</summary>
    private static IEnumerable<IntPtr> AorusGpus()
    {
        foreach (var gpu in NvApi.EnumPhysicalGpus())
        {
            bool isAorus;
            try
            {
                new PanelController(NvApiBusFactory.Panel(gpu)).Probe();
                isAorus = true;
            }
            catch (NvApiException)
            {
                isAorus = false;
            }
            if (isAorus)
            {
                yield return gpu;
            }
        }
    }
}
