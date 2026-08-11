namespace AorusLcd.Core;

/// <summary>Converts pixels to panel little-endian RGB565, fusing channel reorder and 565 pack without an RGB888 buffer.</summary>
public static class Rgb565Encoder
{
    /// <summary>Convert row-major RGB888 (pixelCount * 3) to little-endian RGB565 (pixelCount * 2).</summary>
    public static byte[] Encode(ReadOnlySpan<byte> rgb888)
    {
        int pixels = rgb888.Length / 3;
        var outBuf = new byte[pixels * 2];
        int j = 0;
        for (int i = 0; i < rgb888.Length; i += 3)
        {
            Pack(outBuf.AsSpan(j), rgb888[i], rgb888[i + 1], rgb888[i + 2]);
            j += 2;
        }
        return outBuf;
    }

    /// <summary>BGRA8888 (4 bytes/pixel, B,G,R,A) -> little-endian RGB565, single pass.</summary>
    public static byte[] EncodeBgra(ReadOnlySpan<byte> bgra)
    {
        int pixels = bgra.Length / 4;
        var outBuf = new byte[pixels * 2];
        int j = 0;
        for (int i = 0; i < bgra.Length; i += 4)
        {
            Pack(outBuf.AsSpan(j), bgra[i + 2], bgra[i + 1], bgra[i]); // R,G,B from B,G,R,A
            j += 2;
        }
        return outBuf;
    }

    private static void Pack(Span<byte> dst, byte r, byte g, byte b)
    {
        int v = ((r & 0xF8) << 8) | ((g & 0xFC) << 3) | (b >> 3);
        dst[0] = (byte)(v & 0xFF);
        dst[1] = (byte)((v >> 8) & 0xFF);
    }
}
