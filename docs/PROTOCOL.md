# Aorus LCD Edge View + RGB Fusion 2 - Protocol Reference

A consolidated reference for the reverse-engineered protocols this project
speaks: the **legacy `0x61` LCD** command set (Gigabyte's `GvLcdApi`) and the
**RGB Fusion 2 GPU** lighting protocol.

> **Provenance & legal.** The LCD command surface (opcodes, byte layouts, mode /
> element enums, the LCD-capable SSID list) was recovered by decompiling
> Gigabyte's `ucVga.dll` (`GvLcdApi`) and by capturing live Gigabyte Control
> Center (GCC) I2C traffic. The RGB protocol comes from
> [OpenRGB](https://github.com/CalcProgrammer1/OpenRGB)'s
> `RGBFusion2GPUController`. **This document records facts only - no vendor code
> is reproduced or shipped.** Everything here is tested on exactly one card
> (Aorus Master RTX 5090) and marked accordingly where unconfirmed.

Code cross-references point at the byte-exact implementation in
`src/AorusLcd.Core`.

---

## 1. Transport & addressing

The panel and RGB controller hang off the GPU's **internal I2C controller bus**,
the same one GCC uses.

| | Value |
| --- | --- |
| Windows transport | NVAPI `NvAPI_I2CWriteEx` / `NvAPI_I2CReadEx` (`nvapi64.dll`) |
| Linux transport (reference tool) | `i2c-dev` on the NVIDIA adapter, located by **name** (`NVIDIA i2c adapter 1 at …`), never a guessed `/dev/i2c-N` |
| GPU port | **1** (internal controller bus) |
| Write style | raw block write, **no register/command byte** |
| I2C dev address field | NVAPI wants the address **left-shifted 1** (`Address << 1`) - see `NvApiI2cBus.BuildInfo` |
| LCD bus speed | **400 kHz** (`NV_I2C_SPEED` enum value `NVAPI_I2C_SPEED_400KHZ`, not literal kHz). GCC's `AorusLcdService` pins the panel bus to 400 kHz; the NVAPI `NVAPI_I2C_SPEED_DEFAULT` (0) leaves the controller's current, unspecified speed, at which the panel intermittently rejects otherwise-valid writes (status -1) and the content freezes. The RGB controller (`0x71`/`0x75`) shares the same physical GPU I2C engine, so it is pinned to the same 400 kHz - a default-speed RGB write otherwise wedges the engine and blanks the panel. See `NvApiI2cSpeed`, `NvApiI2cBus.BuildInfo`, `NvApiPanelLocator`, `RgbLocator`. |

### Addresses on the bus

| Address | Device | Notes |
| --- | --- | --- |
| `0x61` | **LCD controller** | All LCD writes target this. |
| `0x71` | RGB controller | Standard RGB Fusion 2 GPU address. |
| `0x75` | RGB controller (RTX 50-series) | The RTX 5090 Master answers here, **write-only** - it ACKs writes but does not reply to reads. RTX 50-series cards use the newer **64-byte Blackwell** protocol here (see §7a); older cards use the legacy 8-byte one at `0x71`. Auto-detected by write-ACK + GPU generation; never issue a read (it wedges the I2C engine). |
| `0x76` | **"LcdEx"** (newer panels) | A different, newer LCD protocol seen in the decompile. **Not implemented** - this project speaks only legacy `0x61`. |

**Presence probe (never write blind):** before any LCD write, send the `EB 03`
status query and require an 8-byte read-back (`PanelController.Probe`). A
zero-length "quick write" probe is *not* used - the NVIDIA adapter rejects it
even when the panel is present, and unconnected DDC ports falsely ACK it.

Code: `Nvapi/NvApiI2cBus.cs`, `Nvapi/NvApiPanelLocator.cs`, `Nvapi/RgbLocator.cs`,
`SystemBusLock.cs` (the cross-process bus lock shared with the background service).

---

## 2. LCD command frame format

Every LCD **command** frame is 256 bytes, zero-padded:

```
[0]      opcode
[1..4]   magic  = CB 55 AC 38
[5..]    params (opcode-specific tail)
[..255]  zero padding
```

Code: `ProtocolFrames.CmdFrame`, `Opcode.Magic`.

### Opcode table (`GvLcdApi`, from `ucVga.dll`)

| Opcode | Name | Tail layout | Notes |
| --- | --- | --- | --- |
| `E7` | OpenLcd | `[5]` = 1 on / 2 off | Panel power. |
| `E5` | SetMode | `[5]` = mode+1 | Mode 7 → internal 9 (GCC quirk). |
| `E1` | SetDisplay | 8 element flags `[5..12]` + interval `[13]` | Sensor-dashboard overlay (see §3). |
| `E3` | SensorFeed | 13-byte sensor block | Live values (see §4). Must be pushed ≈1 Hz. |
| `EA` | SetImageTpl | template block | Overlay color + positions (see §5). |
| `AA` | Save | - | Persist current LCD config to panel NVRAM. |
| `F3` | SetLoop | `[5]` = interval, `[6..]` = (mode+1) list | Carousel play order (modes 0..6). |
| `F2` | UploadMarker | `[5]` = 1 BEGIN / 2 END | Upload framing (see §6). |
| `F1` | UploadHeader | 19-byte header | Upload descriptor (see §6). |
| `D6` | GetFWVersion | read 4 | `"{hi}.{lo}"` from nibble of byte 1. |
| `DE` | GetMode | read 4 | mode = byte1 − 1 (9→7); on = byte2 == 1. |
| `DF` | GetDisplay | read 4 | element bitmask byte1, interval byte2. |
| `F4` | GetLoop | `[5]` = bank 1..5, read 8 | Up to 3 (mode+1) entries per bank + interval byte0. |
| `EB` | GetImageTpl | `[5]` = type, read 8 | `EB 03` doubles as the presence probe. |
| `ED` | GetImageTplData | read 8 | Second half of the template read-back. |
| `FA` | PowerOff | - | `SetPCPowerOffMode` - **experimental**. |

Code: `Opcode.cs`, `PanelController.cs`.

---

## 3. Sensor dashboard - `E1` SetDisplay

Enables/rotates the panel's built-in sensor widgets. Tail is **9 bytes**: one
flag byte per element (in this exact order) followed by the rotation interval.

```
[5] GpuTemp   [6] GpuClock  [7] GpuUsage  [8] FanSpeed
[9] RamClock  [10] RamUsage [11] Fps      [12] Tgp
[13] intervalSeconds
```

Element bitmask (used by `GetDisplay` read-back and `FeedConfig`):

| Bit | Element |
| --- | --- |
| `0x01` | GpuTemp |
| `0x02` | GpuClock |
| `0x04` | GpuUsage |
| `0x08` | FanSpeed |
| `0x10` | RamClock |
| `0x20` | RamUsage |
| `0x40` | Fps |
| `0x80` | Tgp |

Send all-zero flags to turn the dashboard **off** (clean image, no overlay).

Code: `PanelController.SetDisplay`, `LcdDisplayElements.cs`.

> **Note on "brightness".** The reference tool exposes an *experimental*
> `brightness` command that reuses this same `E1` opcode with `[13]` as a
> "value" and all element flags set. Its semantics are **inferred from the
> decompile, not hardware-confirmed**, and in practice it would also enable the
> full sensor overlay. It is deliberately **not** implemented here. LCD backlight
> control may instead live in the unsupported `0x76` "LcdEx" protocol.

---

## 4. Live sensor feed - `E3` SensorFeed

13-byte tail, 16-bit fields **big-endian**. Push continuously (≈1 Hz) or the
widgets freeze.

```
[5]      GpuTemp        (u8,  °C)
[6..7]   GpuClock       (u16, MHz)
[8]      GpuUsage       (u8,  %)
[9..10]  FanSpeed       (u16, RPM)
[11..12] RamClock       (u16, MHz)
[13]     RamUsage       (u8,  %)
[14..15] Fps            (u16)          -- NVML has no FPS source; sent as 0
[16..17] Tgp            (u16, W)       -- whole watts (GCC sent deci-watts; panel prints the raw number)
```

Values are clamped (`u8` 0..255, `u16` 0..65535). The GUI and the background
service share one `SensorFeedLoop`, which pushes E3 at a fixed **~1 Hz**
keep-alive (`SensorFeedTiming.KeepAlivePollMs`). This is independent of the
dashboard **rotation** interval (E1 `[13]`), which the panel firmware handles on
its own: pushing slower than ~1 Hz starves the keep-alive and freezes the
widgets. Gigabyte's own `AorusLcdService` likewise pushes at a fixed 1000 ms.

Code: `PanelController.SendSensorFeed`, `SensorSample.cs`,
`Sensors/NvmlSensorSource.cs`, `Sensors/SensorFeedLoop.cs`.

---

## 5. Overlay template - `EA` SetImageTpl

13-byte tail; positions are 16-bit **big-endian** pairs.

```
[5]      type (1 = Gif, 2 = Image, 3 = Pet)
[6]      color R
[7]      color G
[8]      color B
[9..10]  imageX   [11..12] imageY
[13..14] dataX    [15..16] dataY
[17]     enabled (1/0)
```

Code: `PanelController.SetImageTemplate`, `LcdTemplate` / `LcdTemplateType` in
`LcdStatus.cs` / `LcdMode.cs`.

---

## 6. Content upload (image / text / GIF)

### Panel geometry

- **320 × 170**, little-endian **RGB565**, row-major → 108 800 bytes/frame.
- 12-byte **descriptor** prepended before single-frame pixels (image & text):
  `01 00 0B A9 01 00 40 01 AA 00 01 00` (`Panel.Descriptor`).

### Frame sequence

```
F2(BEGIN, flag=1)  ->  F1 header  ->  256-byte chunks  ->  F2(END, flag=2)
```

**Pacing** the firmware requires (from working captures):

| After | Delay |
| --- | --- |
| BEGIN | 0.5 s |
| F1 header | 1.0 s |
| each chunk | ~10 ms |

Chunk count = `payloadLen / 256 + 1` (an exact 256-multiple still gets one full
pad chunk).

### F1 header (19 bytes, padded to 256)

```
[0]      F1
[1..4]   magic CB 55 AC 38
[5..8]   framebuffer target   (u32 big-endian)
[9]      flag                 (1 = static/text, 2 = gif)
[10..13] chunk count          (u32 big-endian)
[14..15] frame count          (u16 big-endian; 0 for static/text)
[16]     per-frame delay ms   (clamped to 255)
[17]     mode                 (auto: 2 if payload >= 20480 bytes else 1)
[18]     0
```

### Framebuffer targets & display modes

| Content | Framebuffer | SetMode | SetMode order |
| --- | --- | --- | --- |
| Static image | `0x01300000` | 3 | **after** upload |
| Text | `0x01320000` | 4 | **after** upload |
| Animated GIF | `0x00000000` | 5 | **before** upload (live framebuffer) |

Order matters: a GIF streams to a *live* framebuffer, so the panel must already
be in GIF mode as frames arrive; image/text store to numbered framebuffers, so
their `SetMode` goes after.

Code: `ProtocolFrames.cs` (`F2Frame`, `MakeF1Header`, `ChunkPayload`,
`BuildUpload`), `PanelController.SendUploadAsync` / `UploadContentAsync`,
`Panel.cs`, `Rgb565Encoder.cs`, `UploadFrame.cs`.

### Display modes (`E5` / `DisplayMode`)

| Mode | Meaning |
| --- | --- |
| 0-2 | Built-in stat screens (Faith 1/2/3) |
| 3 | Static image |
| 4 | Text |
| 5 | GIF |
| 6 | Chibi clock |
| 7 | Built-in carousel (remapped to internal 9) |

Code: `LcdMode.cs`.

### GIF payload format

```
[frameCount : u16 LE]
table[frameCount] of:
    [endOffset : u32 LE]   -- INCLUSIVE end offset of this frame's RLE blob in the payload
    [w : u16 LE][h : u16 LE][fmt : u16 LE]   -- fmt 3 = RLE
<concatenated RLE frame blobs>
```

`endOffset = 2 + 10*N + sum(size[0..i]) - 1`. There is no checksum anywhere.

**RLE token grammar** (byte-exact mirror of GCC's `Compress_RLE`; do not
"optimize" the quirks - the firmware decoder mirrors it):

- head = `u16 LE`; bit 15 = run flag; low 15 bits = pixel count (may exceed 255).
- run: `<count | 0x8000> <pixel : 2B>` - emitted only for ≥ 3 equal pixels.
- literal: `<count> <count pixels>`.
- Windows of ≤ `0x7FFF` pixels; a window with no repeat, or a trailing tail of
  < 4 pixels, is emitted as a single literal.

Code: `GifPayload.cs`, `RleEncoder.cs`, `Gui/Services/GifDecoder.cs`.

---

## 7. RGB Fusion 2 (GPU lighting)

From OpenRGB's `RGBFusion2GPUController`. **8-byte** raw block packets; the
first byte is a register. The Aorus Master exposes **5 zones**. The controller
NAKs back-to-back writes, so pace ~20 ms between packets (with a small retry).

### Registers

| Byte0 | Register | Meaning |
| --- | --- | --- |
| `0x40` | Color | Single-zone color packet. |
| `0x88` | Mode | Mode header: `[mode, speed, brightness, flag, zone+1, 0, 0]`. |
| `0xB0` | ColorLeftMid | Color packet for zones 0/1. |
| `0xB1` | ColorRight | Color packet for zone 2. |
| `0xB0 + zone*4` | Color banks | Multi-color effect banks (4 pairs/zone). |
| `0xAA` | Save | Persist configuration. |
| `0xAB` | Query | Presence probe (write-ACK only; no read-back). |

### Effect modes

| Value | Mode | Colors |
| --- | --- | --- |
| `0x01` | Static | 1 |
| `0x02` | Breathing | 1 |
| `0x03` | ColorCycle | 0 (rainbow) |
| `0x04` | Flashing | 1 |
| `0x05` | Gradient | 1 |
| `0x06` | ColorShift | multi (color banks) |
| `0x07` | Wave | 1 |
| `0x08` | DualFlashing | 1 |
| `0x0B` | Tricolor | multi (color banks) |

### Ranges

| Field | Range |
| --- | --- |
| Brightness | `0x00`..`0x63` (0..99) |
| Speed | `0x00` (slowest) .. `0x05` (fastest); `0x02` normal |

Code: `Rgb/RgbFusion2.cs`, `Rgb/RgbFusion2Controller.cs`, `Rgb/RgbColor.cs`.

### 7a. RGB Fusion 2 "Blackwell" (RTX 50-series)

RTX 50-series Aorus cards (e.g. the **RTX 5090 Master**, SSID `1458:416E`) answer
at **`0x75`** with a newer **64-byte** protocol (from OpenRGB's
`GigabyteRGBFusion2BlackwellGPUController`), not the legacy 8-byte one above. GCC
itself uses this protocol on these cards. Detection probes both addresses and
uses whichever controller ACKs; the **GPU name** only picks which to try first
(0x75/Blackwell for RTX 50-series, else 0x71/legacy), so a card whose name and
hardware disagree still gets the protocol it actually answers.

64-byte packet layout (byte 0 = register):

```
[0]  register: 0x12 = mode (throttled), 0x16 = direct/color, 0x13 = save
[1]  0x01
[2]  mode
[3]  speed        (0x01 slowest .. 0x06 fastest; 0x03 normal)
[4]  brightness   (0x01 .. 0x0A; breathing forces max)
[5..7] primary R,G,B
[8]  0x00
[9]  zone index
[10] numColors    (>0 only for mode-specific colour effects)
[11..] appended R,G,B triples for mode-specific effects
```

The AORUS 5090/5080 Master "gaming" layout sends **6 zone packets** per update
then a `0x13` save. Modes: Static `0x01`, Direct `0x00`, Breathing `0x02`,
Flashing `0x03`, DualFlashing `0x04`, ColorCycle `0x05`, Wave `0x06`, Gradient
`0x07`, ColorShift `0x08`, Tricolor `0x09`, Dazzle `0x0A`. The controller is
**write-only** (no read-back), so presence is a write-ACK probe and the
generation is decided by name, never a read.

> **Hardware validation status:** confirmed on a physical **RTX 5090 Master** -
> detection (Blackwell @ `0x75`), static colours, the breathing effect, and the
> multi-colour ColorShift cycle all render correctly. The mode-specific colour
> offset was corrected from 12 to **11** during that testing (OpenRGB uses 11 for
> its Master layouts).

Code: `Rgb/RgbFusion2Blackwell.cs`, `Rgb/RgbFusion2BlackwellController.cs`,
`Nvapi/RgbLocator.cs`.

---

## 8. Card support

`GvLcdApi.IsSupportLcd` gates on the GPU **subsystem ID (SSID)**. The known
LCD-capable Aorus SSIDs are listed in `LcdSupport.cs`.

---

## 9. Experimental / unconfirmed

These have semantics inferred from the decompile but are **not** hardware-confirmed:

- **`E1` "brightness"** - see §3; not implemented (would toggle the dashboard).
- **`FA` PowerOff** (`SetPCPowerOffMode`).
- **`0x76` "LcdEx"** newer-panel protocol - different transport, unimplemented.

---

## 10. References

- `albancreton/aorus-master-linux` - the reference Linux implementation this
  project ports (legacy `0x61` protocol).
- `CalcProgrammer1/OpenRGB` - RGB Fusion 2 GPU protocol and NVAPI I2C struct
  layout.
- `EKYavsil/aorus-lcd-panel-reverse-engineering` - additional RE notes.
