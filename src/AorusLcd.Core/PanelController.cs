using System.Buffers.Binary;

namespace AorusLcd.Core;

/// <summary>High-level panel operations with GCC pacing: 0.5 s after BEGIN, 1.0 s after F1, ~10 ms between chunks.</summary>
public sealed class PanelController(II2cBus bus)
{
    private const int PaceBeginMs = 500;
    private const int PaceHeaderMs = 1000;
    private const int GifModeSettleMs = 200;
    public const int DefaultChunkDelayMs = 10;

    private readonly II2cBus _bus = bus;

    /// <summary>One raw I2C write of a 256-byte frame to the controller.</summary>
    public void WriteFrame(ReadOnlySpan<byte> data) => _bus.Write(data);

    /// <summary>Write a command frame, then read <paramref name="nbytes"/> back.</summary>
    public byte[] ReadCommand(byte opcode, ReadOnlySpan<byte> tail, int nbytes = 8)
    {
        WriteFrame(ProtocolFrames.CmdFrame(opcode, tail));
        return _bus.Read(nbytes);
    }

    /// <summary>Send EB 03 status query and return the 8-byte read-back; throws if the controller does not answer.</summary>
    public byte[] Probe() => ReadCommand(Opcode.Probe, [0x03]);

    public void OpenLcd(bool on)
        => WriteFrame(ProtocolFrames.CmdFrame(Opcode.OpenLcd, [(byte)(on ? 1 : 2)]));

    /// <summary>AA Save: persist the current LCD configuration to panel NVRAM.</summary>
    public void Save() => WriteFrame(ProtocolFrames.CmdFrame(Opcode.Save));

    /// <summary>Trigger built-in text rainbow by committing the same 0xAA frame as <see cref="Save"/> while in text mode.</summary>
    public void ApplyTextEffect() => WriteFrame(ProtocolFrames.CmdFrame(Opcode.Save));

    /// <summary>E5 SetMode: byte5 = mode+1. Mode 7 maps to internal 9 (GCC quirk).</summary>
    public void SetMode(int mode)
        => WriteFrame(ProtocolFrames.CmdFrame(Opcode.SetMode, [(byte)((mode == 7 ? 9 : mode) + 1)]));

    /// <summary>E1 SetDisplay: one flag byte for GpuTemp/GpuClock/GpuUsage/FanSpeed/RamClock/RamUsage/Fps/Tgp, then interval.</summary>
    public void SetDisplay(LcdDisplayElements elements, int intervalSeconds)
    {
        var tail = new byte[9];
        for (int i = 0; i < 8; i++)
        {
            if (((uint)elements & (1u << i)) != 0)
            {
                tail[i] = 1;
            }
        }
        tail[8] = (byte)intervalSeconds;
        WriteFrame(ProtocolFrames.CmdFrame(Opcode.SetDisplay, tail));
    }

    /// <summary>F3 SetLoop: byte5 = arg, byte6.. = (mode+1) in play order. Modes 0..6.</summary>
    public void SetCarousel(IEnumerable<int> modes, int arg = 0)
    {
        var tail = new List<byte> { (byte)(arg & 0xFF) };
        foreach (int m in modes)
        {
            if (m is >= 0 and <= 6)
            {
                tail.Add((byte)((m & 0xFF) + 1));
            }
        }
        WriteFrame(ProtocolFrames.CmdFrame(Opcode.SetLoop, tail.ToArray()));
    }

    public void PowerOffMode() => WriteFrame(ProtocolFrames.CmdFrame(Opcode.PowerOff));

    /// <summary>E3 feed layout: temp, GPU clock(2), usage, fan(2), RAM clock(2), RAM usage, FPS(2), TGP(2), 16-bit big-endian, ~1 Hz.</summary>
    public void SendSensorFeed(SensorSample s)
    {
        Span<byte> tail = stackalloc byte[13];
        tail[0] = Clamp8(s.GpuTempC);
        BinaryPrimitives.WriteUInt16BigEndian(tail[1..], Clamp16(s.GpuClockMhz));
        tail[3] = Clamp8(s.GpuUsagePercent);
        BinaryPrimitives.WriteUInt16BigEndian(tail[4..], Clamp16(s.FanSpeed));
        BinaryPrimitives.WriteUInt16BigEndian(tail[6..], Clamp16(s.RamClockMhz));
        tail[8] = Clamp8(s.RamUsagePercent);
        BinaryPrimitives.WriteUInt16BigEndian(tail[9..], Clamp16(s.Fps));
        BinaryPrimitives.WriteUInt16BigEndian(tail[11..], Clamp16(s.TgpWatts));
        WriteFrame(ProtocolFrames.CmdFrame(Opcode.SensorFeed, tail));
    }

    /// <summary>EA SetImageTpl layout: type, RGB, image X/Y and data X/Y as 16-bit big-endian pairs, plus enable flag.</summary>
    public void SetImageTemplate(LcdTemplate template)
    {
        Span<byte> tail = stackalloc byte[13];
        tail[0] = (byte)template.Type;
        tail[1] = template.ColorR;
        tail[2] = template.ColorG;
        tail[3] = template.ColorB;
        BinaryPrimitives.WriteUInt16BigEndian(tail[4..], Clamp16(template.ImagePosition.X));
        BinaryPrimitives.WriteUInt16BigEndian(tail[6..], Clamp16(template.ImagePosition.Y));
        BinaryPrimitives.WriteUInt16BigEndian(tail[8..], Clamp16(template.DataPosition.X));
        BinaryPrimitives.WriteUInt16BigEndian(tail[10..], Clamp16(template.DataPosition.Y));
        tail[12] = (byte)(template.Enabled ? 1 : 0);
        WriteFrame(ProtocolFrames.CmdFrame(Opcode.SetImageTpl, tail));
    }

    // ---- read-back commands (write the query frame, then read bytes) -----------

    /// <summary>D6 GetFWVersion: panel firmware version as "major.minor".</summary>
    public string GetFirmwareVersion()
    {
        var r = ReadCommand(Opcode.GetFwVersion, [], 4);
        return $"{r[1] >> 4}.{r[1] & 0xF}";
    }

    /// <summary>DE GetMode: current display mode and on/off state.</summary>
    public (LcdMode Mode, bool On) GetMode()
    {
        var r = ReadCommand(Opcode.GetMode, [], 4);
        int mode = r[1] - 1;
        if (mode == 9)
        {
            mode = 7;
        }
        return ((LcdMode)mode, r[2] == 1);
    }

    /// <summary>DF GetDisplay: current dashboard element bitmask and interval.</summary>
    public (LcdDisplayElements Elements, int Interval) GetDisplay()
    {
        var r = ReadCommand(Opcode.GetDisplay, [], 4);
        var elements = LcdDisplayElements.None;
        for (int i = 0; i < 8; i++)
        {
            if ((r[1] & (1 << i)) != 0)
            {
                elements |= (LcdDisplayElements)(1u << i);
            }
        }
        return (elements, r[2]);
    }

    /// <summary>Read GUI status, covering firmware, mode, and dashboard elements (carousel banks are not read back).</summary>
    public LcdStatus GetStatus()
    {
        var (mode, on) = GetMode();
        var (elements, interval) = GetDisplay();
        return new LcdStatus
        {
            FirmwareVersion = GetFirmwareVersion(),
            Mode = mode,
            IsOn = on,
            DisplayElements = elements,
            DisplayInterval = interval,
        };
    }

    private static byte Clamp8(int v) => (byte)Math.Clamp(v, 0, 255);

    private static ushort Clamp16(int v) => (ushort)Math.Clamp(v, 0, ushort.MaxValue);

    /// <summary>Write upload frames paced by <see cref="UploadFrameKind"/>, never by frame byte content.</summary>
    public async Task SendUploadAsync(IReadOnlyList<UploadFrame> frames,
        int chunkDelayMs = DefaultChunkDelayMs, CancellationToken cancellationToken = default)
    {
        foreach (var frame in frames)
        {
            WriteFrame(frame.Data);
            int delayMs = frame.Kind switch
            {
                UploadFrameKind.Begin => PaceBeginMs,
                UploadFrameKind.Header => PaceHeaderMs,
                _ => chunkDelayMs,
            };
            await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Stream and select mode: GIF SetMode before upload; image/text SetMode after, because their framebuffers differ.</summary>
    public async Task UploadContentAsync(IReadOnlyList<UploadFrame> frames, int mode, bool isGif,
        bool setDisplayMode = true, int chunkDelayMs = DefaultChunkDelayMs,
        CancellationToken cancellationToken = default)
    {
        if (isGif && setDisplayMode)
        {
            SetMode(mode);
            await Task.Delay(GifModeSettleMs, cancellationToken).ConfigureAwait(false);
        }
        await SendUploadAsync(frames, chunkDelayMs, cancellationToken).ConfigureAwait(false);
        if (!isGif && setDisplayMode)
        {
            SetMode(mode);
        }
    }
}
