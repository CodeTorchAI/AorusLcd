using System.Globalization;
using System.Runtime.Versioning;
using AorusLcd.Core;
using AorusLcd.Core.Nvapi;
using AorusLcd.Core.Rgb;
[assembly: SupportedOSPlatform("windows")]

// AorusLcd panel recovery / control utility.
//
// The recovery sequence itself lives in AorusLcd.Core.PanelRecovery, shared with the
// GUI's "Recover panel" button. This is the scriptable front end for it.
//
// Runbook: docs/RECOVERY.md.
//
// Usage:
//   lcd-recovery                    Full recovery into Faith 1 with TGP+GPU-temp overlay.
//   lcd-recovery --status           Read mode/on-state/overlay/firmware and change nothing.
//   lcd-recovery --mode <n>         Target display mode (0=Faith1..7=Carousel). Default 0.
//   lcd-recovery --color RRGGBB     Fill color for the repaint frame (hex). Default 000000 (black).
//   lcd-recovery --no-powercycle    Skip the OpenLcd off/on power cycle.
//   lcd-recovery --no-upload        Skip the framebuffer upload. Rarely enough on its own.
//   lcd-recovery --no-save          Do not persist to panel NVRAM.
//   lcd-recovery --no-overlay       Leave the existing E1 sensor overlay untouched.
//   lcd-recovery --clear-overlay    Send E1 with every widget disabled.

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("This recovery tool needs Windows (NVAPI).");
    return 1;
}

var options = new RecoveryOptions();
bool statusOnly = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--mode" when i + 1 < args.Length:
            if (!int.TryParse(args[++i], CultureInfo.InvariantCulture, out int targetMode) ||
                !Enum.IsDefined((LcdMode)targetMode))
            {
                Console.Error.WriteLine($"--mode must be 0..{(int)LcdMode.Carousel}, got '{args[i]}'.");
                return 1;
            }
            options = options with { TargetMode = (LcdMode)targetMode };
            break;
        case "--color" when i + 1 < args.Length:
            if (!RgbColor.TryParse(args[++i], out var fillColor))
            {
                Console.Error.WriteLine($"--color must be 6 hex digits (RRGGBB), got '{args[i]}'.");
                return 1;
            }
            options = options with { FillColor = fillColor };
            break;
        case "--no-powercycle":
            options = options with { PowerCycle = false };
            break;
        case "--no-upload":
            options = options with { Upload = false };
            break;
        case "--no-save":
            options = options with { Save = false };
            break;
        case "--no-overlay":
            options = options with { Overlay = OverlayAction.Leave };
            break;
        case "--clear-overlay":
            options = options with { Overlay = OverlayAction.Clear };
            break;
        case "--status":
            statusOnly = true;
            break;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            return 1;
    }
}

// Hold the shared bus lock across the whole run, including the EB 03 locate probe,
// so a concurrent GUI/service write cannot interleave with it.
using var busLock = new SystemBusLock();
IDisposable acquired;
try
{
    acquired = busLock.Acquire();
}
catch (TimeoutException)
{
    Console.Error.WriteLine(
        "Timed out waiting for the bus lock. Something else is holding it: stop AorusLcdFeed " +
        "(this project) or AorusLcdService (GIGABYTE Control Center), then retry.");
    return 4;
}
using var busScope = acquired;

Console.WriteLine("Locating the Aorus LCD controller (0x61)...");
var located = NvApiPanelLocator.Locate();
if (located is null)
{
    Console.Error.WriteLine(
        "No panel answered EB 03. Is the NVIDIA driver present and are you running as Administrator?");
    return 2;
}

var (rawBus, gpuName) = located.Value;
Console.WriteLine($"Panel found on {gpuName}.");

// Same decorator the GUI/service use: retries transient NVAPI status -1 so a
// single flaky write during the upload doesn't leave the panel half-painted.
var panel = new PanelController(new RetryingI2cBus(rawBus));

if (statusOnly)
{
    // Reads are not retried by the decorator, so absorb the intermittent -1 here.
    for (int attempt = 1; attempt <= 8; attempt++)
    {
        try
        {
            var s = panel.GetStatus();
            Console.WriteLine(
                $"Mode={s.Mode}, On={s.IsOn}, Overlay={s.DisplayElements}, " +
                $"Interval={s.DisplayInterval}s, Firmware={s.FirmwareVersion}.");
            return 0;
        }
        catch (NvApiException)
        {
            Thread.Sleep(300);
        }
    }
    Console.Error.WriteLine("Could not read panel status after 8 attempts (flaky I2C reads).");
    return 3;
}

// Block synchronously: the bus lock is a thread-affine mutex, so Main must not hop
// threads via await between Acquire and release, or ReleaseMutex throws.
PanelRecovery
    .RunAsync(panel, options, new ConsoleProgress())
    .GetAwaiter().GetResult();

try
{
    var status = panel.GetStatus();
    Console.WriteLine(
        $"Done. Mode={status.Mode}, On={status.IsOn}, Overlay={status.DisplayElements}, " +
        $"Interval={status.DisplayInterval}s, Firmware={status.FirmwareVersion}.");
}
catch (NvApiException ex)
{
    // The final status read-back is informational only and runs after every write
    // (including Save) has landed. NvAPI's I2C read intermittently returns -1 on this
    // controller, so a failure here does not mean the recovery failed.
    Console.WriteLine($"Done (writes applied). Status read-back skipped: {ex.Message}");
}
return 0;

/// <summary>Writes progress synchronously; <see cref="Progress{T}"/> posts asynchronously and would reorder console lines.</summary>
internal sealed class ConsoleProgress : IProgress<string>
{
    public void Report(string value) => Console.WriteLine(value);
}
