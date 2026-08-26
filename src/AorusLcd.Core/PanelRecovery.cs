using AorusLcd.Core.Rgb;

namespace AorusLcd.Core;

/// <summary>
/// The blank/wedged panel repair from docs/RECOVERY.md: power-cycle, upload a fresh frame to clear
/// the framebuffer, then select the target mode. An <c>E5</c> SetMode alone does not repaint,
/// because the wedged content lives in the framebuffer and only a real <c>F2</c>/<c>F1</c> upload clears it.
/// </summary>
public static class PanelRecovery
{
    private const int PowerCycleSettleMs = 1000;
    private const int UploadSettleMs = 500;
    private const int ModeSettleMs = 300;
    private const int OverlayIntervalSeconds = 3;

    private const LcdDisplayElements DashboardWidgets = LcdDisplayElements.GpuTemp | LcdDisplayElements.Tgp;

    /// <summary>Run the recovery sequence, reporting each step through <paramref name="progress"/>.</summary>
    public static async Task RunAsync(PanelController panel, RecoveryOptions options,
        IProgress<string>? progress = null, TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(panel);
        ArgumentNullException.ThrowIfNull(options);
        var time = timeProvider ?? TimeProvider.System;

        if (options.PowerCycle)
        {
            progress?.Report("Power-cycling the LCD...");
            panel.OpenLcd(false);
            await DelayAsync(PowerCycleSettleMs, time, cancellationToken).ConfigureAwait(false);
            panel.OpenLcd(true);
            await DelayAsync(PowerCycleSettleMs, time, cancellationToken).ConfigureAwait(false);
        }

        if (options.Upload)
        {
            var color = options.FillColor;
            progress?.Report($"Uploading a fresh frame (#{color.R:X2}{color.G:X2}{color.B:X2}) to clear the wedge...");
            var frames = ProtocolFrames.BuildUpload(Panel.Descriptor, SolidFrame(color), Panel.FramebufferStatic);
            await panel.UploadContentAsync(frames, Panel.ModeStatic, isGif: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            await DelayAsync(UploadSettleMs, time, cancellationToken).ConfigureAwait(false);
        }

        progress?.Report($"Switching to {options.TargetMode}...");
        if ((int)options.TargetMode == Panel.ModeStatic)
        {
            // The upload already left the panel in Image mode, so nudge through another
            // mode first to make the SetMode below a genuine change that re-renders.
            panel.SetMode((int)LcdMode.ChibTime);
            await DelayAsync(ModeSettleMs, time, cancellationToken).ConfigureAwait(false);
        }
        panel.SetMode((int)options.TargetMode);
        await DelayAsync(ModeSettleMs, time, cancellationToken).ConfigureAwait(false);

        switch (options.Overlay)
        {
            case OverlayAction.Clear:
                progress?.Report("Clearing the sensor overlay...");
                panel.SetDisplay(LcdDisplayElements.None, intervalSeconds: 0);
                break;
            case OverlayAction.Enable:
                progress?.Report("Enabling the GPU-temp + TGP overlay...");
                panel.SetDisplay(DashboardWidgets, OverlayIntervalSeconds);
                break;
        }

        if (options.Save)
        {
            progress?.Report("Saving to panel NVRAM...");
            panel.Save();
        }
    }

    private static Task DelayAsync(int milliseconds, TimeProvider time, CancellationToken cancellationToken)
        => Task.Delay(TimeSpan.FromMilliseconds(milliseconds), time, cancellationToken);

    /// <summary>Build a full-panel little-endian RGB565 frame filled with one color.</summary>
    private static byte[] SolidFrame(RgbColor color)
    {
        var rgb888 = new byte[Panel.FramePixels * 3];
        for (int i = 0; i < rgb888.Length; i += 3)
        {
            rgb888[i] = color.R;
            rgb888[i + 1] = color.G;
            rgb888[i + 2] = color.B;
        }
        return Rgb565Encoder.Encode(rgb888);
    }
}
