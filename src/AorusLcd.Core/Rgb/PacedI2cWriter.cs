namespace AorusLcd.Core.Rgb;

/// <summary>Shared paced write for RGB Fusion 2 controllers: write, delay, and retry because the controller NAKs back-to-back writes.</summary>
internal static class PacedI2cWriter
{
    /// <summary>Write one packet, pacing by <paramref name="delayMs"/> and retrying up to <paramref name="attempts"/> times.</summary>
    public static void Send(II2cBus bus, byte[] packet, int attempts, int delayMs)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                bus.Write(packet);
                if (delayMs > 0)
                {
                    Thread.Sleep(delayMs);
                }
                return;
            }
            catch (Exception) when (attempt < attempts)
            {
                Thread.Sleep(delayMs > 0 ? delayMs : 10);
            }
        }
    }
}
