# AorusLcd

A lightweight, open-source controller for the **Gigabyte Aorus Master RTX 5090**
"LCD Edge View" panel **and its RGB Fusion 2 lighting** - a low-overhead
replacement for Gigabyte Control Center's LCD/RGB features, with a
**cross-platform GUI** and a lightweight background **Windows service**.

This started as a Windows port of the Linux tool
[`albancreton/aorus-master-linux`](https://github.com/albancreton/aorus-master-linux),
whose reverse-engineered `0x61` LCD protocol it reproduces byte-for-byte, plus
the RGB Fusion 2 GPU protocol from
[OpenRGB](https://github.com/CalcProgrammer1/OpenRGB). The full LCD command set
was recovered by decompiling Gigabyte's `ucVga.dll` (facts only - no vendor code
is shipped). It is **alpha, community software**, tested on a single card (Aorus
Master RTX 5090). Not affiliated with or endorsed by Gigabyte or NVIDIA.

## What is this?

AorusLcd controls the two customizable surfaces on the Aorus Master RTX 5090:
the **LCD Edge View** panel on the side of the card and its **RGB Fusion 2**
lighting. It does everything Gigabyte Control Center's LCD/RGB modules do for
that hardware, but as a small, focused, open-source app with no telemetry, no
always-on suite, and no elevated lighting service. Set your content once and
quit; only the optional live sensor dashboard needs a tiny background service.

## Features

**LCD panel**

- **Static image** - any image, resized to 320x170 and sent as the background.
- **Text** - custom message with color, background color, and size, plus an
  optional **rainbow effect** the panel applies over the text (on by default).
- **Animated GIF** - decoded, RLE-compressed, and streamed to the panel.
- **Built-in screens** - Gigabyte's Faith 1/2/3 animations and the Chibi clock.
- **Carousel** - auto-rotate through any selection of screens.
- **Sensor dashboard** - the panel's built-in overlay of live GPU widgets:
  GPU temp, GPU clock, GPU usage, fan speed, RAM clock, RAM usage, FPS, and TGP,
  with a configurable rotation interval.
- **Save to panel** - persist the current content/config to the panel's NVRAM
  so it survives a reboot with nothing running.
- **Panel power** - turn the LCD on or off.

**RGB Fusion 2 lighting**

- **Two protocols, auto-selected by GPU** - the newer **64-byte "Blackwell"**
  controller for RTX 50-series cards (e.g. the RTX 5090 Master) and the legacy
  **8-byte** controller (5 zones) for older cards. The GUI shows the matching RGB
  tab for your card.
- **Effects** - static, breathing/pulse, color cycle, flash, wave, gradient,
  color shift, dual/double flash, and more (the exact set depends on the card).
- **Multi-color** - the color-blend effects take several colors; the others one.
- **Brightness and speed** controls.

**Application**

- Cross-platform **Avalonia** GUI (Windows / Linux / macOS) with a built-in
  color picker.
- Minimizes to the **system tray**; optional **start with Windows** (launches
  straight to the tray).
- **In-app updates** - the About tab checks GitHub Releases and, when a newer
  version exists, downloads its installer and launches it in one click (Windows).
- A lightweight **background service** drives the live sensor feed so the
  dashboard keeps updating with the GUI closed and no user logged in.
- The GUI and service share the I2C bus through a **cross-process lock**, so an
  image upload never collides with a sensor frame.

## How it compares to Gigabyte Control Center

AorusLcd is a lightweight replacement for GCC's LCD and RGB modules for this
card. It is not a full system suite: fan curves, overclocking, and BIOS updates
are out of scope by design.

### Footprint and overhead

| | AorusLcd | Gigabyte Control Center |
| --- | --- | --- |
| Installer | single self-contained exe ~106 MB (or ~30 MB framework-dependent + the .NET 10 runtime) | ~855 MB |
| Installed on disk | ~106 MB (+ ~5 MB service) | ~900 MB to 1.5 GB |
| Always-on background | **one** NativeAOT service (~5 MB), and only if you use the live dashboard | 2-3 processes + **2 services** (GCCService, LightingService) |
| Idle RAM | service in the low tens of MB (NativeAOT); GUI can be fully quit | ~150-300 MB across resident services |
| Telemetry / updates | none | auto-start, update pinging, profile sync |
| Elevation | unelevated GUI; one UAC prompt only to install the service | elevated services resident |

AorusLcd binary sizes above are measured on `win-x64` (Release); GCC figures are
from its published installer and typical resident-process usage. The headline is
roughly an 8-10x smaller install and a fraction of the idle footprint.

For static content (image / text / GIF / RGB / built-in screens) you can quit
the app entirely - the panel keeps the content because it is saved to NVRAM.
The only resident piece is the small sensor-feed service, and only when the live
dashboard is enabled.

Upload speed is at parity by design: the panel firmware dictates the pacing
(0.5 s after BEGIN, 1.0 s after the header, ~10 ms per chunk), so neither tool
can push frames faster. Where AorusLcd wins is resource overhead and idle cost.

### Feature parity

| Feature | AorusLcd | GCC |
| --- | --- | --- |
| Static image / text / GIF | Yes | Yes |
| Short video loop | No | Yes |
| Built-in screens + carousel | Yes | Yes |
| Sensor dashboard | Yes | Yes |
| FPS widget | Sends 0 (NVML has no FPS source) | Yes |
| LCD brightness | No (no confirmed opcode in the `0x61` protocol) | Yes |
| Save to panel NVRAM | Yes | Yes |
| GPU RGB Fusion 2 (5 zones, 9 effects) | Yes | Yes |
| Whole-system RGB sync (board/peripherals) | No (GPU only, by design) | Yes |
| Fan curves / overclocking / BIOS | No (out of scope) | Yes |

On the panel protocol itself the coverage is essentially complete: the full
recovered `GvLcdApi` command set is implemented.

### Known gaps / roadmap

- **FPS widget** - the only dashboard value that is currently always 0; NVML has
  no FPS source, so it needs a feed from PresentMon (ETW) or RTSS.
- **Video loops** - could reuse the GIF path with a video decoder (FFmpeg).
- **LCD brightness** - no confirmed command in the `0x61` protocol; the only
  known candidate is an experimental, hardware-unconfirmed reuse of `E1` that
  would also toggle the dashboard, so it is intentionally not shipped.
- **Multi-image slideshow** and **saved profiles** - convenience features GCC
  has that would be easy additions.
- **Linux hardware backend** - the UI and imaging are already cross-platform; an
  i2c-dev transport is planned to match the reference tool.

## Install

Grab the latest [release](https://github.com/JustinMDotNet/AorusLcd/releases) and
either:

- **Installer (recommended)** - run `AorusLcd-<version>-setup.exe`. It installs to
  Program Files, adds a Start Menu shortcut, and registers an Add/Remove Programs
  entry (which also removes the background service on uninstall).
- **Portable zip** - `AorusLcd-<version>-win-x64-self-contained.zip` (no
  prerequisites) or the smaller framework-dependent zip (needs the .NET 10
  runtime). Extract and run `AorusLcd.Gui.exe`.

The optional live sensor dashboard needs the background service; install it once
from the app's **Device** tab (one UAC prompt).

## GUI

`AorusLcd.Gui` is an [Avalonia](https://avaloniaui.net) desktop app (Windows /
Linux / macOS) with three tabs - **Device**, **LCD Panel** (image, text, GIF,
built-in screens, carousel, sensor dashboard), and **RGB Lighting**. It
minimizes to the **system tray** and can **start with Windows** (launching
straight to the tray). The panel's live GPU widgets (temp, TGP, clocks…) are
driven by a lightweight background **Windows service** (`AorusLcdFeed`, a
self-contained NativeAOT ~5 MB exe) that reads GPU sensors via NVML and pushes
the feed even when the GUI is closed - the GUI just installs/manages the service
and writes the shared dashboard config it consumes. Hardware control currently
requires Windows (NVAPI); the UI and imaging are cross-platform, with a Linux
i2c-dev backend planned.

```powershell
dotnet run --project src\AorusLcd.Gui
```

### Do I need to leave it running?

No - for a static image, text, GIF, RGB color/effect, or the built-in screens,
set it once (tick **Save to panel** to persist across reboots) and quit; the
panel and RGB controller keep displaying on their own. The **only** exception is
the **live sensor dashboard**: those widgets are just numbers the panel shows, so
something must keep pushing the sensor feed (~1 Hz) or they freeze. That job
belongs to the background **`AorusLcdFeed` service**, not the GUI - install it
once from the **Device** tab and the dashboard keeps updating with the GUI
closed and no user logged in. The GUI and the service coordinate on the shared
I2C bus through a cross-process lock, so an image upload never collides with a
sensor frame.

### Building a distributable

Publish the service first, then the GUI, and bundle the service exe next to the
GUI (NativeAOT needs an MSVC toolchain / `vcvars64`):

```powershell
# NativeAOT background service (~5 MB self-contained native exe)
dotnet publish src\AorusLcd.Service -c Release -r win-x64 -o publish\service

# GUI - self-contained single exe (~106 MB, no prerequisites)
dotnet publish src\AorusLcd.Gui -c Release -r win-x64 --self-contained `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:DebugType=none -o publish\self-contained

# bundle the service next to the GUI so the app (and installer) can find it
Copy-Item publish\service\AorusLcd.Service.exe publish\self-contained\

# framework-dependent (~30 MB, needs the .NET 10 runtime)
dotnet publish src\AorusLcd.Gui -c Release -r win-x64 --no-self-contained `
  -p:PublishSingleFile=true -p:DebugType=none -o publish\framework-dependent
```

To build the Windows **installer** (`setup.exe`), compile the
[Inno Setup](https://jrsoftware.org/isinfo.php) script against the
`publish\self-contained\` output produced above - it packages the GUI and the
bundled service, adds a Start Menu shortcut and an Add/Remove Programs entry, and
removes the background service on uninstall:

```powershell
iscc /DMyAppVersion=1.0.0 installer\AorusLcd.iss
# -> installer\output\AorusLcd-1.0.0-setup.exe
```

Tagged releases build all of the above automatically (see
[`.github/workflows/release.yml`](.github/workflows/release.yml)) and attach the
`setup.exe` plus both zips to the GitHub Release.

## How it works

> **Full protocol reference:** see [`docs/PROTOCOL.md`](docs/PROTOCOL.md) for the
> complete, consolidated LCD (`0x61` `GvLcdApi`) and RGB Fusion 2 command
> reference - frame layouts, opcode tables, the upload/GIF/RLE formats, address
> quirks, and what's still experimental.

The panel hangs off the GPU's internal I2C controller bus, behind the same
controller GCC talks to. On Windows we reach that bus through **NVAPI**
(`nvapi64.dll`): `NvAPI_I2CWriteEx` / `NvAPI_I2CReadEx` on GPU **port 1**, using
raw block writes (no register address) - the same path OpenRGB uses.

- Address `0x61` = LCD controller (all LCD writes target this).
- Address `0x71`/`0x75` = RGB controller. On the RTX 5090 Master it answers at
  **`0x75`** (write-only - it ACKs writes but does not reply to reads), so the
  tool auto-detects by write-ACK and never issues a read that would wedge the
  I2C engine.
- Panel: 320x170, little-endian RGB565, row-major.
- Upload: `F2(BEGIN)` → `F1` header → 256-byte chunks → `F2(END)`, then `E5` SetMode.
- RGB: RTX 50-series (Blackwell) cards use 64-byte packets (`0x12`/`0x16`, save
  `0x13`); older cards use 8-byte packets (mode `0x88`, color `0x40`/`0xB0`/`0xB1`,
  save `0xAA`) across 5 zones. The GPU name picks which protocol to probe first;
  detection then uses whichever controller actually responds.

Before any write, the tool sends the `EB 03` status query and requires a
read-back, so it never writes to a bus that does not answer.

## Requirements

- Windows with an NVIDIA driver (provides `nvapi64.dll`).
- .NET 10 SDK.
- Run from an account able to reach the GPU I2C bus (Administrator recommended).

## Build & test

```powershell
dotnet build
dotnet test                       # encoder/protocol byte-parity tests
```

## Usage

Launch the GUI and drive everything from its three tabs:

```powershell
dotnet run --project src\AorusLcd.Gui
```

- **Device** - connect (Refresh), panel on/off, save-to-NVRAM, "Start with
  Windows", and install/start/stop the background **`AorusLcdFeed`** service.
- **LCD Panel** - send a static image, rendered text, or an animated GIF; pick a
  built-in screen; configure the carousel; and choose which sensor widgets the
  dashboard shows. Tick **Save to panel** to persist across reboots.
- **RGB Lighting** - static color or an effect (breathing, color cycle, flash,
  wave, gradient, color shift, dual flash, tricolor) with brightness/speed. The
  multi-color effects (color shift, tricolor) take up to three colors.

The live sensor dashboard is pushed by the background service, so enable it once
(Device tab → Install) and the widgets keep updating with the GUI closed.

Opcodes and layouts were confirmed by decompiling Gigabyte's `ucVga.dll`
(`GvLcdApi`) - facts only, no vendor code is included or shipped:

| Opcode | Command | Notes |
| --- | --- | --- |
| `E7` | OpenLcd | byte5 = 1 on / 2 off |
| `E5` | SetMode | byte5 = mode+1; mode 7 → internal 9 |
| `E1` | SetDisplay | 8 sensor-element flags + interval byte (the dashboard overlay) |
| `EA` | SetImageTpl | overlay template: type, color, image/data positions, enable |
| `AA` | Save | persist current LCD config to panel NVRAM |
| `F3` | SetLoop | carousel: interval + (mode+1) list |
| `F2`/`F1` | Upload | begin/header for image/text/gif frames |
| `E3` | Sensor feed | GCC's service pushes live GPU stats here every second |
| `D6`/`DE`/`DF`/`F4` | Get FW / Mode / Display / Loop | read-back commands (used by `status`) |
| `EB 03` | GetImageTpl | read PET template; a valid read-back doubles as the presence probe |

The full LCD command surface (opcodes, byte layouts, mode/element enums, the
LCD-capable SSID list) was recovered from Gigabyte's `ucVga.dll` `GvLcdApi` and
reimplemented from scratch - facts only, no vendor code is included or shipped.

Display modes: `0-2` built-in stat screens, `3` image, `4` text, `5` gif,
`6` chibi, `7` carousel. The rotating TGP/temp overlay is the **E1 sensor
dashboard** (`Display` bitmask GpuTemp=1 … Tgp=0x80), independent of the
display mode - `sensors off` clears it, and `image` clears it by default
(pass `--keep-sensors` to keep it).

## Projects

| Project | Purpose |
| --- | --- |
| `src/AorusLcd.Core` | Protocol frames, RLE + GIF payload, RGB565/GDI+ content, `II2cBus`, `PanelController`, NVAPI transport, NVML sensors, shared feed config + `SystemBusLock`. |
| `src/AorusLcd.Gui` | Avalonia cross-platform GUI (Device / LCD Panel / RGB tabs, tray, service management). |
| `src/AorusLcd.Service` | NativeAOT background Windows service (`AorusLcdFeed`) that pushes the live sensor feed. |
| `tests/AorusLcd.Tests` | xUnit byte-parity tests. |

## Safety

Nothing here flashes firmware or touches persistent GPU state beyond the panel's
own image memory. The worst observed failure during development of the reference
tool was stale/black panel content until the next upload or a power cycle -
never a bricked card. Still: use at your own risk.

## Credits

- Protocol reverse engineering: `albancreton/aorus-master-linux`.
- NVAPI I2C approach and struct layout: OpenRGB (`CalcProgrammer1/OpenRGB`).

## License

Released under the [MIT License](LICENSE) - the same license as the
[`albancreton/aorus-master-linux`](https://github.com/albancreton/aorus-master-linux)
project this tool ports.

**Third-party notices.** This project is a clean-room reimplementation that
records *protocol facts only* - opcodes, register values, byte layouts, and
enums - and ships no third-party or vendor code:

- Protocol constants and the NVAPI I2C struct layout used for RGB Fusion 2 were
  taken as factual reference from OpenRGB
  (`CalcProgrammer1/OpenRGB`, GPL-2.0). Only non-copyrightable facts (register
  addresses, mode values) are used; no OpenRGB source is copied or distributed.
- The `0x61` LCD command surface was recovered by decompiling Gigabyte's
  `ucVga.dll` (`GvLcdApi`) and by capturing live GCC I2C traffic - **facts only,
  no vendor code is included or shipped**.

Not affiliated with or endorsed by Gigabyte or NVIDIA.
