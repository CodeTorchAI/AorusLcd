namespace AorusLcd.Core.Rgb;

/// <summary>Per-zone effect configuration passed to the controller.</summary>
public sealed class RgbZoneConfig
{
    public byte Brightness { get; set; } = RgbFusion2.BrightnessMax;
    public byte Speed { get; set; } = RgbFusion2.SpeedNormal;
    public RgbColor[] Colors { get; set; } = [RgbColor.Black];
    public byte NumberOfColors => (byte)Colors.Length;
}

/// <summary>Byte-exact OpenRGB RGBFusion2 GPU controller port using raw 8-byte I2C writes, usually at 0x71 or RTX 50-series 0x75.</summary>
public sealed class RgbFusion2Controller(II2cBus bus)
{
    private readonly RgbColor[] _zoneColor = new RgbColor[4];

    /// <summary>Delay after each packet; the controller NAKs back-to-back writes.</summary>
    public int WriteDelayMs { get; set; } = 20;

    /// <summary>Probe by ACKed <c>AB</c> write only; RTX 5090 Master read failures leave GPU I2C in an error state.</summary>
    public bool Detect()
    {
        try
        {
            bus.Write([RgbFusion2.RegQuery, 0, 0, 0, 0, 0, 0, 0]);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Set a single static color on every zone, then persist it.</summary>
    public void SetStatic(RgbColor color, byte brightness)
    {
        var config = new RgbZoneConfig { Brightness = brightness, Colors = [color] };
        for (byte zone = 0; zone < RgbFusion2.ZoneCount; zone++)
        {
            SetZone(zone, RgbMode.Static, config);
        }
        SaveConfig();
    }

    /// <summary>Apply an effect mode with the given colors/speed/brightness to all zones.</summary>
    public void SetEffect(RgbMode mode, RgbColor[] colors, byte speed, byte brightness)
    {
        var config = new RgbZoneConfig
        {
            Brightness = brightness,
            Speed = speed,
            Colors = colors.Length > 0 ? colors : [RgbColor.Black],
        };
        for (byte zone = 0; zone < RgbFusion2.ZoneCount; zone++)
        {
            SetZone(zone, mode, config);
        }
        SaveConfig();
    }

    /// <summary>Turn lighting off (static black at zero brightness).</summary>
    public void Off() => SetStatic(RgbColor.Black, RgbFusion2.BrightnessMin);

    /// <summary>Persist the current configuration to the controller (AA).</summary>
    public void SaveConfig() => Send([RgbFusion2.RegSave, 0, 0, 0, 0, 0, 0, 0]);

    /// <summary>Write one 8-byte packet with pacing and a small retry.</summary>
    private void Send(byte[] packet) => PacedI2cWriter.Send(bus, packet, attempts: 3, WriteDelayMs);

    private void SetZone(byte zone, RgbMode mode, RgbZoneConfig config)
    {
        switch (mode)
        {
            case RgbMode.Static:
            case RgbMode.Breathing:
            case RgbMode.Flashing:
            case RgbMode.DualFlashing:
            case RgbMode.Wave:
                SetModePackets(zone, mode, config, mysteryFlag: 0x00);
                break;

            case RgbMode.Gradient:
                SetModePackets(zone, mode, config, mysteryFlag: 0x08);
                break;

            case RgbMode.ColorCycle:
                WriteModeHeader(zone, mode, config, mysteryFlag: 0x00);
                break;

            case RgbMode.ColorShift:
                WriteModeHeader(zone, mode, config, mysteryFlag: config.NumberOfColors);
                WriteColorBanks(zone, mode, config);
                break;

            case RgbMode.Tricolor:
                WriteModeHeader(zone, mode, config, mysteryFlag: 0x08);
                WriteColorBanks(zone, mode, config);
                break;
        }
    }

    private void SetModePackets(byte zone, RgbMode mode, RgbZoneConfig config, byte mysteryFlag)
    {
        if (zone < 4)
        {
            _zoneColor[zone] = config.Colors[0];
        }
        WriteModeHeader(zone, mode, config, mysteryFlag);

        byte[] colorPacket = zone switch
        {
            0 or 1 =>
            [
                RgbFusion2.RegColorLeftMid, (byte)mode,
                _zoneColor[0].R, _zoneColor[0].G, _zoneColor[0].B,
                _zoneColor[1].R, _zoneColor[1].G, _zoneColor[1].B,
            ],
            2 =>
            [
                RgbFusion2.RegColorRight, (byte)mode,
                _zoneColor[2].R, _zoneColor[2].G, _zoneColor[2].B, 0, 0, 0,
            ],
            _ =>
            [
                RgbFusion2.RegColor,
                config.Colors[0].R, config.Colors[0].G, config.Colors[0].B,
                (byte)(zone + 1), 0, 0, 0,
            ],
        };
        Send(colorPacket);
    }

    private void WriteModeHeader(byte zone, RgbMode mode, RgbZoneConfig config, byte mysteryFlag)
        => Send([RgbFusion2.RegMode, (byte)mode, config.Speed, config.Brightness, mysteryFlag, (byte)(zone + 1), 0, 0]);

    private void WriteColorBanks(byte zone, RgbMode mode, RgbZoneConfig config)
    {
        byte bank = (byte)(0xB0 + (zone * 4));
        for (byte pair = 0; pair < 4; pair++)
        {
            var a = ColorAt(config, pair * 2);
            var b = ColorAt(config, (pair * 2) + 1);
            Send([(byte)(bank + pair), (byte)mode, a.R, a.G, a.B, b.R, b.G, b.B]);
        }
    }

    private static RgbColor ColorAt(RgbZoneConfig config, int index)
        => index < config.Colors.Length ? config.Colors[index] : RgbColor.Black;
}
