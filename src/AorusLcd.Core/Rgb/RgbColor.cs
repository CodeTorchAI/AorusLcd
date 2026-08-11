namespace AorusLcd.Core.Rgb;

/// <summary>An 8-bit-per-channel RGB color.</summary>
public readonly record struct RgbColor(byte R, byte G, byte B)
{
    public static RgbColor Black => new(0, 0, 0);

    /// <summary>Try to parse an <c>RRGGBB</c> hex string (leading '#' optional).</summary>
    public static bool TryParse(string? hex, out RgbColor color)
    {
        color = Black;
        if (hex is null)
        {
            return false;
        }
        var s = hex.AsSpan().TrimStart('#');
        if (s.Length != 6)
        {
            return false;
        }
        if (byte.TryParse(s[..2], System.Globalization.NumberStyles.HexNumber, null, out byte r)
            && byte.TryParse(s[2..4], System.Globalization.NumberStyles.HexNumber, null, out byte g)
            && byte.TryParse(s[4..], System.Globalization.NumberStyles.HexNumber, null, out byte b))
        {
            color = new RgbColor(r, g, b);
            return true;
        }
        return false;
    }
}
