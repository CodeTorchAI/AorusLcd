using AorusLcd.Core;
using AorusLcd.Core.Rgb;

namespace AorusLcd.Tests;

/// <summary>Verifies the <see cref="PanelRecovery"/> command sequence documented in docs/RECOVERY.md.</summary>
public class PanelRecoveryTests
{
    // Command frames are [opcode, CB 55 AC 38, params...], so the first parameter sits at index 5.
    private const int ArgIndex = 5;

    private static Task RunAsync(RecordingBus bus, RecoveryOptions options)
        => PanelRecovery.RunAsync(new PanelController(bus), options, timeProvider: new NoDelayTimeProvider());

    private static RecoveryOptions NoUpload => new() { Upload = false };

    [Fact]
    public async Task PowerCycles_Off_Then_On_Before_Repainting()
    {
        var bus = new RecordingBus();

        await RunAsync(bus, NoUpload);

        Assert.Equal(Opcode.OpenLcd, bus.Writes[0][0]);
        Assert.Equal(2, bus.Writes[0][ArgIndex]); // 2 = off
        Assert.Equal(Opcode.OpenLcd, bus.Writes[1][0]);
        Assert.Equal(1, bus.Writes[1][ArgIndex]); // 1 = on
    }

    [Fact]
    public async Task Skips_PowerCycle_When_Disabled()
    {
        var bus = new RecordingBus();

        await RunAsync(bus, NoUpload with { PowerCycle = false });

        Assert.DoesNotContain(bus.Writes, w => w[0] == Opcode.OpenLcd);
    }

    [Fact]
    public async Task Uploads_A_Fresh_Frame_Then_Selects_The_Target_Mode()
    {
        var bus = new RecordingBus();

        await RunAsync(bus, new RecoveryOptions { FillColor = new RgbColor(0xFF, 0, 0) });

        // An E5 SetMode alone cannot repaint a wedged panel, so the upload must be present.
        Assert.Contains(bus.Writes, w => w[0] == Opcode.UploadMarker);
        Assert.Contains(bus.Writes, w => w[0] == Opcode.UploadHeader);

        // The upload leaves the panel in Image mode; the target mode is selected after it.
        int lastUpload = bus.Writes.FindLastIndex(w => w[0] == Opcode.UploadMarker);
        int targetMode = bus.Writes.FindLastIndex(w => w[0] == Opcode.SetMode);
        Assert.True(targetMode > lastUpload);
        Assert.Equal((int)LcdMode.Faith1 + 1, bus.Writes[targetMode][ArgIndex]);
    }

    [Fact]
    public async Task Nudges_Through_Another_Mode_When_Targeting_Image()
    {
        var bus = new RecordingBus();

        await RunAsync(bus, NoUpload with { TargetMode = LcdMode.Image });

        var modes = bus.Writes.FindAll(w => w[0] == Opcode.SetMode);
        Assert.Equal(2, modes.Count);
        Assert.Equal((int)LcdMode.ChibTime + 1, modes[0][ArgIndex]);
        Assert.Equal((int)LcdMode.Image + 1, modes[1][ArgIndex]);
    }

    [Fact]
    public async Task Enables_The_Dashboard_Widgets_By_Default()
    {
        var bus = new RecordingBus();

        await RunAsync(bus, NoUpload);

        var overlay = Assert.Single(bus.Writes.FindAll(w => w[0] == Opcode.SetDisplay));
        Assert.Equal(1, overlay[ArgIndex]); // bit 0 = GpuTemp
        Assert.Equal(1, overlay[ArgIndex + 7]); // bit 7 = Tgp
        Assert.Equal(3, overlay[ArgIndex + 8]); // rotation interval
    }

    [Fact]
    public async Task Sends_No_Overlay_Command_When_Leaving_It_Alone()
    {
        var bus = new RecordingBus();

        await RunAsync(bus, NoUpload with { Overlay = OverlayAction.Leave });

        Assert.DoesNotContain(bus.Writes, w => w[0] == Opcode.SetDisplay);
    }

    [Fact]
    public async Task Clearing_The_Overlay_Disables_Every_Widget()
    {
        var bus = new RecordingBus();

        await RunAsync(bus, NoUpload with { Overlay = OverlayAction.Clear });

        var overlay = Assert.Single(bus.Writes.FindAll(w => w[0] == Opcode.SetDisplay));
        Assert.All(overlay[ArgIndex..(ArgIndex + 9)], b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task Saves_Last_So_NVRAM_Captures_The_Repaired_State()
    {
        var bus = new RecordingBus();

        await RunAsync(bus, NoUpload);

        Assert.Equal(Opcode.Save, bus.Writes[^1][0]);
    }

    [Fact]
    public async Task Skips_Save_When_Disabled()
    {
        var bus = new RecordingBus();

        await RunAsync(bus, NoUpload with { Save = false });

        Assert.DoesNotContain(bus.Writes, w => w[0] == Opcode.Save);
    }

    private sealed class RecordingBus : II2cBus
    {
        public List<byte[]> Writes { get; } = [];

        public void Write(ReadOnlySpan<byte> data) => Writes.Add(data.ToArray());

        public byte[] Read(int count) => new byte[count];

        public void Dispose()
        {
        }
    }

    /// <summary>Fires every timer straight away so the recovery pacing does not slow the suite down.</summary>
    private sealed class NoDelayTimeProvider : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state,
            TimeSpan dueTime, TimeSpan period)
            => new ImmediateTimer(callback, state);

        private sealed class ImmediateTimer : ITimer
        {
            // Queued rather than invoked inline, so the callback cannot re-enter the
            // Task.Delay machinery that is still building this timer.
            public ImmediateTimer(TimerCallback callback, object? state)
                => ThreadPool.QueueUserWorkItem(_ => callback(state));

            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
