using System.Globalization;
using System.Runtime.Versioning;
using AorusLcd.Core;
using AorusLcd.Core.Nvapi;
[assembly: SupportedOSPlatform("windows")]

// AorusLcd panel recovery / control utility.
//
// A blanked/wedged panel does NOT repaint on an E5 SetMode alone: the wedged
// content lives in the framebuffer/NVRAM and only a real F2/F1 upload clears it.
// The upload leaves the panel in Image mode, so the SetMode that follows is a
// genuine mode change. Targeting Image itself is not, hence the nudge below.
//
// Recovery sequence:
//   power-cycle -> upload a fresh static frame (clears the wedge, enters Image
//   mode) -> transition to the target mode (default Faith1) -> enable the
//   TGP+GPU-temp overlay -> Save to NVRAM.
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

int targetMode = (int)LcdMode.Faith1;
byte r = 0, g = 0, b = 0;
bool powerCycle = true;
bool upload = true;
bool save = true;
bool overlay = true;
bool clearOverlay = false;
bool statusOnly = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--mode" when i + 1 < args.Length:
            if (!int.TryParse(args[++i], CultureInfo.InvariantCulture, out targetMode) ||
                !Enum.IsDefined((LcdMode)targetMode))
            {
                Console.Error.WriteLine($"--mode must be 0..{(int)LcdMode.Carousel}, got '{args[i]}'.");
                return 1;
            }
            break;
        case "--color" when i + 1 < args.Length:
            var hex = args[++i].TrimStart('#');
            if (hex.Length != 6 ||
                !byte.TryParse(hex.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r) ||
                !byte.TryParse(hex.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g) ||
                !byte.TryParse(hex.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b))
            {
                Console.Error.WriteLine($"--color must be 6 hex digits (RRGGBB), got '{args[i]}'.");
                return 1;
            }
            break;
        case "--no-powercycle":
            powerCycle = false;
            break;
        case "--no-upload":
            upload = false;
            break;
        case "--no-save":
            save = false;
            break;
        case "--no-overlay":
            overlay = false;
            break;
        case "--clear-overlay":
            overlay = false;
            clearOverlay = true;
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
using var busScope = busLock.Acquire();

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

if (powerCycle)
{
    Console.WriteLine("Power-cycling the LCD...");
    panel.OpenLcd(false);
    Thread.Sleep(1000);
    panel.OpenLcd(true);
    Thread.Sleep(1000);
}

if (upload)
{
    Console.WriteLine($"Uploading a fresh static frame (#{r:X2}{g:X2}{b:X2}) to clear the wedge...");
    var frame = SolidFrame(r, g, b);
    var frames = ProtocolFrames.BuildUpload(Panel.Descriptor, frame, Panel.FramebufferStatic);
    // Block synchronously: the bus lock is a thread-affine mutex, so Main must not
    // hop threads via await between Acquire and release, or ReleaseMutex throws.
    panel.UploadContentAsync(frames, Panel.ModeStatic, isGif: false).GetAwaiter().GetResult();
    Thread.Sleep(500);
}

Console.WriteLine($"Transitioning to mode {(LcdMode)targetMode}...");
if (targetMode == Panel.ModeStatic)
{
    // The upload already left the panel in Image mode; nudge through another mode
    // so the SetMode below is a genuine change and actually re-renders.
    panel.SetMode((int)LcdMode.ChibTime);
    Thread.Sleep(300);
}
panel.SetMode(targetMode);
Thread.Sleep(300);

if (clearOverlay)
{
    Console.WriteLine("Clearing the sensor overlay (no widgets)...");
    panel.SetDisplay(LcdDisplayElements.None, intervalSeconds: 0);
}
else if (overlay)
{
    Console.WriteLine("Enabling the TGP + GPU-temp dashboard overlay...");
    panel.SetDisplay(LcdDisplayElements.GpuTemp | LcdDisplayElements.Tgp, intervalSeconds: 3);
}

if (save)
{
    Console.WriteLine("Saving to panel NVRAM...");
    panel.Save();
}

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

// Build a 320x170 little-endian RGB565 frame filled with one color.
static byte[] SolidFrame(byte r, byte g, byte b)
{
    var rgb888 = new byte[Panel.FramePixels * 3];
    for (int i = 0; i < rgb888.Length; i += 3)
    {
        rgb888[i] = r;
        rgb888[i + 1] = g;
        rgb888[i + 2] = b;
    }
    return Rgb565Encoder.Encode(rgb888);
}
