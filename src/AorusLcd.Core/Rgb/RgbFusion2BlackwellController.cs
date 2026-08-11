namespace AorusLcd.Core.Rgb;

/// <summary>Port of OpenRGB's RGBFusion2BlackwellGPUController: drives the RTX 50-series Aorus GPU RGB controller (0x75) with 64-byte writes.</summary>
public sealed class RgbFusion2BlackwellController(II2cBus bus)
{
    /// <summary>Delay after each packet; the controller NAKs back-to-back writes.</summary>
    public int WriteDelayMs { get; set; } = 20;

    /// <summary>Probe by write-ACK of the harmless query packet; no read is issued (the controller is write-only).</summary>
    public bool Detect()
    {
        try
        {
            var packet = new byte[RgbFusion2Blackwell.PacketSize];
            packet[0] = RgbFusion2Blackwell.RegQuery;
            packet[1] = 0x01;
            bus.Write(packet);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Set a single static colour on every zone, then persist it.</summary>
    public void SetStatic(RgbColor color, byte brightness)
        => Apply(RgbBlackwellMode.Static, [color], RgbFusion2Blackwell.SpeedNormal, brightness);

    /// <summary>Apply an effect mode with the given colours/speed/brightness to all zones.</summary>
    public void SetEffect(RgbBlackwellMode mode, RgbColor[] colors, byte speed, byte brightness)
        => Apply(mode, colors.Length > 0 ? colors : [RgbColor.Black], speed, brightness);

    /// <summary>Turn lighting off (static black at minimum brightness).</summary>
    public void Off() => SetStatic(RgbColor.Black, RgbFusion2Blackwell.BrightnessMin);

    /// <summary>Persist the current configuration to the controller (0x13).</summary>
    public void SaveConfig()
    {
        var packet = new byte[RgbFusion2Blackwell.PacketSize];
        packet[0] = RgbFusion2Blackwell.RegSave;
        packet[1] = 0x01;
        Send(packet);
    }

    private void Apply(RgbBlackwellMode mode, RgbColor[] colors, byte speed, byte brightness)
    {
        for (byte zone = 0; zone < RgbFusion2Blackwell.GamingLayoutZones; zone++)
        {
            SetZone(zone, mode, colors, speed, brightness);
        }
        SaveConfig();
    }

    /// <summary>Build and send one 64-byte zone packet in OpenRGB's SetMode layout.</summary>
    private void SetZone(byte zone, RgbBlackwellMode mode, RgbColor[] colors, byte speed, byte brightness)
    {
        // Breathing ignores brightness on the hardware, so force it to max (matches OpenRGB).
        if (mode == RgbBlackwellMode.Breathing)
        {
            brightness = RgbFusion2Blackwell.BrightnessMax;
        }

        byte type = mode == RgbBlackwellMode.Direct
            ? RgbFusion2Blackwell.RegColor
            : RgbFusion2Blackwell.RegMode;
        // Cap to what actually fits so numColors never advertises more triples than the packet carries.
        byte numColors = IsMultiColor(mode)
            ? (byte)Math.Min(colors.Length, RgbFusion2Blackwell.MaxColors)
            : (byte)0;

        var packet = new byte[RgbFusion2Blackwell.PacketSize];
        packet[0] = type;
        packet[1] = 0x01;
        packet[2] = (byte)mode;
        packet[3] = speed;
        packet[4] = brightness;
        packet[5] = colors[0].R;
        packet[6] = colors[0].G;
        packet[7] = colors[0].B;
        packet[8] = 0x00;
        packet[9] = zone;
        packet[10] = numColors;

        int pos = RgbFusion2Blackwell.ColorDataOffset;
        for (int i = 0; i < numColors && pos + 3 <= RgbFusion2Blackwell.PacketSize; i++)
        {
            packet[pos] = colors[i].R;
            packet[pos + 1] = colors[i].G;
            packet[pos + 2] = colors[i].B;
            pos += 3;
        }
        Send(packet);
    }

    /// <summary>Modes whose colours travel in the appended mode-specific colour list.</summary>
    private static bool IsMultiColor(RgbBlackwellMode mode)
        => mode is RgbBlackwellMode.ColorShift or RgbBlackwellMode.Tricolor or RgbBlackwellMode.Dazzle;

    /// <summary>Write one packet with pacing and a small retry (the controller NAKs back-to-back writes).</summary>
    private void Send(byte[] packet) => PacedI2cWriter.Send(bus, packet, attempts: 3, WriteDelayMs);
}
