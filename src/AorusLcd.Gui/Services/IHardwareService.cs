using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AorusLcd.Core;
using AorusLcd.Core.Nvapi;
using AorusLcd.Core.Rgb;

namespace AorusLcd.Gui.Services;

/// <summary>Async facade over the Aorus LCD panel and RGB controllers; injectable seam for the view model.</summary>
public interface IHardwareService : IPanelRecovery
{
    bool IsSupportedPlatform { get; }

    string GpuName { get; }

    /// <summary>The detected RGB protocol generation, or null until RGB is located.</summary>
    RgbControllerKind? RgbKind { get; }

    Task<string> ConnectAsync();

    Task<LcdStatus> GetStatusAsync();

    Task SendImageAsync(byte[] le565, bool clearSensors, bool save, CancellationToken ct = default);

    Task SendTextAsync(byte[] le565, bool clearSensors, bool save, bool rainbowEffect,
        CancellationToken ct = default);

    Task SendGifAsync(IReadOnlyList<byte[]> le565Frames, IReadOnlyList<int> delaysMs,
        bool save, CancellationToken ct = default);

    Task SetSensorsAsync(LcdDisplayElements elements, int intervalSeconds, bool save);

    Task SetCarouselAsync(IReadOnlyList<int> modes, int intervalSeconds, bool save);

    Task SetPanelPowerAsync(bool isOn);

    Task SetModeAsync(LcdMode mode);

    Task SaveAsync();

    Task<(string GpuName, byte Address, RgbControllerKind Kind)> ConnectRgbAsync();

    Task SetRgbStaticAsync(RgbColor color, byte brightness);

    Task SetRgbEffectAsync(RgbMode mode, RgbColor[] colors, byte speed, byte brightness);

    Task RgbOffAsync();

    Task SetRgbBlackwellStaticAsync(RgbColor color, byte brightness);

    Task SetRgbBlackwellEffectAsync(RgbBlackwellMode mode, RgbColor[] colors, byte speed, byte brightness);

    Task RgbBlackwellOffAsync();
}
