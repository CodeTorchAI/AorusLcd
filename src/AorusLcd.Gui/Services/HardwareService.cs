using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AorusLcd.Core;
using AorusLcd.Core.Nvapi;
using AorusLcd.Core.Rgb;

namespace AorusLcd.Gui.Services;

/// <summary>Raised when no supported hardware/transport is available.</summary>
public sealed class HardwareUnavailableException(string message) : Exception(message);

/// <summary>GUI async facade over core hardware using NVAPI and full-operation <see cref="SystemBusLock"/>; controllers are cached.</summary>
public sealed class HardwareService : IHardwareService
{
    private readonly Lazy<SystemBusLock> _busLock = new(() => new SystemBusLock());
    private PanelController? _panel;
    private string _gpuName = "-";
    private RgbFusion2Controller? _rgb;
    private RgbFusion2BlackwellController? _rgbBlackwell;
    private RgbControllerKind? _rgbKind;
    private byte _rgbAddress;

    public bool IsSupportedPlatform => OperatingSystem.IsWindows();

    public string GpuName => _gpuName;

    // ---- LCD ---------------------------------------------------------------

    public Task<string> ConnectAsync() => WithPanelAsync(_ => _gpuName);

    public Task<LcdStatus> GetStatusAsync() => WithPanelAsync(panel => panel.GetStatus());

    public Task SendImageAsync(byte[] le565, bool clearSensors, bool save, CancellationToken ct = default)
        => SendSingleFrameAsync(le565, Panel.FramebufferStatic, Panel.ModeStatic, clearSensors, save, ct);

    public Task SendTextAsync(byte[] le565, bool clearSensors, bool save, bool rainbowEffect,
        CancellationToken ct = default)
        => SendSingleFrameAsync(le565, Panel.FramebufferText, Panel.ModeText, clearSensors, save, ct,
            applyTextEffect: rainbowEffect);

    /// <summary>Upload one still frame (image or text) to a numbered framebuffer and select its mode.</summary>
    private Task SendSingleFrameAsync(byte[] le565, uint framebuffer, int mode,
        bool clearSensors, bool save, CancellationToken ct, bool applyTextEffect = false)
        => WithPanelAsync(panel =>
        {
            var frames = ProtocolFrames.BuildUpload(Panel.Descriptor, le565, framebuffer);
            panel.UploadContentAsync(frames, mode, isGif: false, cancellationToken: ct)
                .GetAwaiter().GetResult();
            if (applyTextEffect)
            {
                panel.ApplyTextEffect();
            }
            if (clearSensors)
            {
                panel.SetDisplay(LcdDisplayElements.None, 0);
            }
            if (save)
            {
                panel.Save();
            }
        });

    public Task SendGifAsync(IReadOnlyList<byte[]> le565Frames, IReadOnlyList<int> delaysMs,
        bool save, CancellationToken ct = default)
        => WithPanelAsync(panel =>
        {
            var (payload, count, delayMs) = GifPayload.Build(le565Frames, delaysMs, null);
            var frames = ProtocolFrames.BuildUpload(payload, Panel.FramebufferGif,
                flag: 2, nframes: (ushort)count, delay: delayMs, mode: 2);
            panel.UploadContentAsync(frames, Panel.ModeGif, isGif: true, cancellationToken: ct)
                .GetAwaiter().GetResult();
            if (save)
            {
                panel.Save();
            }
        });

    public Task SetSensorsAsync(LcdDisplayElements elements, int intervalSeconds, bool save)
        => WithPanelAsync(panel =>
        {
            panel.SetDisplay(elements, intervalSeconds);
            if (save)
            {
                panel.Save();
            }
        });

    public Task SetCarouselAsync(IReadOnlyList<int> modes, int intervalSeconds, bool save)
        => WithPanelAsync(panel =>
        {
            panel.SetCarousel(modes, intervalSeconds);
            if (save)
            {
                panel.Save();
            }
        });

    public Task SetPanelPowerAsync(bool isOn) => WithPanelAsync(panel => panel.OpenLcd(isOn));

    public Task SetModeAsync(LcdMode mode) => WithPanelAsync(panel => panel.SetMode((int)mode));

    /// <summary>Run the docs/RECOVERY.md repaint sequence against a blank or frozen panel.</summary>
    public Task RecoverPanelAsync(RecoveryOptions options, IProgress<string>? progress = null,
        CancellationToken ct = default)
        // Blocks inside the locked worker thread, matching the upload path: the bus lock is a
        // thread-affine mutex, so the thread that acquired it must be the one that releases it.
        => WithPanelAsync(panel => PanelRecovery
            .RunAsync(panel, options, progress, cancellationToken: ct)
            .GetAwaiter().GetResult());

    public Task SaveAsync() => WithPanelAsync(panel => panel.Save());

    // ---- RGB ---------------------------------------------------------------

    /// <summary>The detected RGB protocol generation, or null until RGB is located.</summary>
    public RgbControllerKind? RgbKind => _rgbKind;

    public Task<(string GpuName, byte Address, RgbControllerKind Kind)> ConnectRgbAsync()
        => WithRgbLocatedAsync(() => (_gpuName, _rgbAddress, _rgbKind!.Value));

    // Legacy 8-byte controller (pre-Blackwell cards).
    public Task SetRgbStaticAsync(RgbColor color, byte brightness)
        => WithLegacyRgbAsync(rgb => rgb.SetStatic(color, brightness));

    public Task SetRgbEffectAsync(RgbMode mode, RgbColor[] colors, byte speed, byte brightness)
        => WithLegacyRgbAsync(rgb => rgb.SetEffect(mode, colors, speed, brightness));

    public Task RgbOffAsync() => WithLegacyRgbAsync(rgb => rgb.Off());

    // Blackwell 64-byte controller (RTX 50-series cards).
    public Task SetRgbBlackwellStaticAsync(RgbColor color, byte brightness)
        => WithBlackwellRgbAsync(rgb => rgb.SetStatic(color, brightness));

    public Task SetRgbBlackwellEffectAsync(RgbBlackwellMode mode, RgbColor[] colors, byte speed, byte brightness)
        => WithBlackwellRgbAsync(rgb => rgb.SetEffect(mode, colors, speed, brightness));

    public Task RgbBlackwellOffAsync() => WithBlackwellRgbAsync(rgb => rgb.Off());

    // ---- internals ---------------------------------------------------------

    private Task WithPanelAsync(Action<PanelController> action)
        => WithPanelAsync(panel => { action(panel); return true; });

    private Task<T> WithPanelAsync<T>(Func<PanelController, T> action) => Task.Run(() =>
    {
        using (AcquireBus())
        {
            try
            {
                return action(EnsurePanel());
            }
            catch (NvApiException)
            {
                _panel = null; // force re-locate next time
                throw;
            }
        }
    });

    private Task WithLegacyRgbAsync(Action<RgbFusion2Controller> action)
        => WithRgbLocatedAsync(() => { action(EnsureLegacyRgb()); return true; });

    private Task WithBlackwellRgbAsync(Action<RgbFusion2BlackwellController> action)
        => WithRgbLocatedAsync(() => { action(EnsureBlackwellRgb()); return true; });

    private Task<T> WithRgbLocatedAsync<T>(Func<T> action) => Task.Run(() =>
    {
        using (AcquireBus())
        {
            try
            {
                EnsureRgbLocated();
                return action();
            }
            catch (NvApiException)
            {
                _rgb = null;
                _rgbBlackwell = null;
                _rgbKind = null;
                throw;
            }
        }
    });

    /// <summary>Acquire Windows bus lock on the worker thread that will dispose it; unsupported platforms no-op before friendly failure.</summary>
    private IDisposable AcquireBus()
        => OperatingSystem.IsWindows() ? _busLock.Value.Acquire() : NoOpDisposable.Instance;

    private PanelController EnsurePanel()
    {
        RequireWindows();
        if (_panel is not null)
        {
            return _panel;
        }
        var located = NvApiPanelLocator.Locate();
        if (located is null)
        {
            throw new HardwareUnavailableException(
                "No Aorus LCD found (no GPU answered the status query at 0x61 on port 1).");
        }
        _gpuName = located.Value.GpuName;
        _panel = new PanelController(new RetryingI2cBus(located.Value.Bus));
        return _panel;
    }

    /// <summary>Locate the RGB controller once, caching its generation, address, and the matching driver.</summary>
    private void EnsureRgbLocated()
    {
        RequireWindows();
        if (_rgbKind is not null)
        {
            return;
        }
        var located = RgbLocator.LocateBus();
        if (located is null)
        {
            throw new HardwareUnavailableException(
                "No Aorus RGB controller found (no ACK on 0x71/0x75, port 1).");
        }
        _gpuName = located.Value.GpuName;
        _rgbAddress = located.Value.Address;
        _rgbKind = located.Value.Kind;
        if (located.Value.Kind == RgbControllerKind.Blackwell)
        {
            _rgbBlackwell = new RgbFusion2BlackwellController(located.Value.Bus);
        }
        else
        {
            _rgb = new RgbFusion2Controller(located.Value.Bus);
        }
    }

    private RgbFusion2Controller EnsureLegacyRgb()
        => _rgb ?? throw new HardwareUnavailableException(
            "This GPU uses the Blackwell RGB protocol; use the Blackwell RGB controls.");

    private RgbFusion2BlackwellController EnsureBlackwellRgb()
        => _rgbBlackwell ?? throw new HardwareUnavailableException(
            "This GPU uses the legacy RGB protocol; use the legacy RGB controls.");

    private void RequireWindows()
    {
        if (!IsSupportedPlatform)
        {
            throw new HardwareUnavailableException(
                "Hardware control currently requires Windows (NVAPI). A Linux i2c-dev backend is planned.");
        }
    }
}
