# Panel recovery

The LCD goes dark or freezes for a few different reasons, and they do not share a
fix. Work down this page in order. Each step tells you what its outcome rules out.

Everything here uses `tools/lcd-recovery`, a standalone console utility that talks
to the panel through `AorusLcd.Core`. It sits outside the solution, so build it
explicitly:

```powershell
dotnet build tools\lcd-recovery -c Release
$lcd = "tools\lcd-recovery\bin\Release\net10.0\lcd-recovery.exe"
```

## 1. Read the panel state

```powershell
& $lcd --status
```

A healthy panel answers like this:

```
Panel found on NVIDIA GeForce RTX 5090.
Mode=Faith1, On=True, Overlay=GpuTemp, Tgp, Interval=3s, Firmware=1.3.
```

| Output | Meaning |
| --- | --- |
| `No panel answered EB 03` | The probe write or its read-back failed. Check that the NVIDIA driver is loaded, and retry elevated. |
| `Mode=-1` | The `DE` read-back returned a zero mode byte. Usually bus contention with another process polling `0x61`, and harmless on its own. |
| `Could not read panel status after 8 attempts` | One of the `DE`, `DF`, or `D6` reads kept failing. Contention is the usual cause. Go to step 2. |
| Status looks correct, screen still dark | Not a configuration problem. Go to step 3, then step 4. |

Reading status is safe and writes nothing, so run it before you stop anything.
Expect contention noise in the numbers while GCC is still running.

## 2. Stop whatever else owns the bus

Exactly one process should drive `0x61` while you recover. The tool holds
`Global\AorusLcdBusLock`, and this project's own GUI and service respect it, but
GIGABYTE's software knows nothing about that mutex.

| Software | Service name |
| --- | --- |
| This project | `AorusLcdFeed` |
| GIGABYTE Control Center | `AorusLcdService` |

Stop the one that is installed, elevated, and confirm it actually stopped before
you write anything to the panel:

```powershell
sc.exe stop AorusLcdFeed
sc.exe stop AorusLcdService
sc.exe query AorusLcdService        # wait for STATE : 1 STOPPED
```

Start it again when you are done. Without a running feed the overlay still
renders, but the temperature and TGP numbers freeze at whatever was last pushed.

## 3. Wedged framebuffer

This is the common failure. The panel reports a sane mode and `On=True` while
showing a frozen image or nothing at all.

An `E5` SetMode alone will not repaint it. The wedged content lives in the
framebuffer, so only a real `F2`/`F1` upload clears it. The upload leaves the
panel in Image mode, which is what makes the `SetMode` that follows a genuine
mode change. Asking for Image itself is the one case that is not, so the tool
nudges through another mode first when you pass `--mode 3`.

```powershell
& $lcd --mode 0 --no-save
```

Use `--no-save` whenever GCC owns the configuration, so its settings survive in
NVRAM. Drop the flag if this project is driving the panel and you want the state
to persist across a reboot.

## 4. Dark panel that reports healthy

Symptom: every read comes back correct and there is no light at all, in any mode.

This happened on 2026-08-22. The panel reported `Mode=Faith3, On=True` with the
overlay set, and stayed black. A full recovery back into Faith 3 changed nothing.
Neither did a solid white upload in Image mode with GCC's service stopped, which
was the useful test, because it ruled out both a content wedge and GCC as the
culprit. What actually brought it back:

```powershell
& $lcd --mode 0 --color FF0000 --no-overlay --no-save
```

Faith 1 is a firmware animation that needs no uploaded content, so it is the
cleanest test of whether the panel can light up at all. Be careful about the
conclusion though, because that run was also the third power-cycle in a row, and
nothing proves the target mode rather than the repeated power cycling was what
fixed it. Try it either way before blaming the hardware.

There is no backlight command to fall back on. The reference tool's `brightness`
reuses the `E1` opcode with semantics inferred from a decompile, and it would
also force the full sensor overlay on, so this project does not implement it.
Real backlight control probably lives in the unimplemented `0x76` LcdEx protocol.
See [PROTOCOL.md](PROTOCOL.md).

## 5. If nothing lights it

Cold power cycle. Shut down fully, then flip the PSU switch off or pull the cord
for about 30 seconds. A reboot is not enough, because the module keeps standby
power across one.

## Flags

| Flag | Effect |
| --- | --- |
| `--status` | Read mode, on-state, overlay, interval, and firmware. Changes nothing. |
| `--mode <n>` | Target mode, `0`=Faith1 through `7`=Carousel. Default `0`. |
| `--color RRGGBB` | Fill color for the repaint frame, 6 hex digits. Default `000000`. |
| `--no-powercycle` | Skip the `E7` off/on. |
| `--no-upload` | Skip the framebuffer upload. The power-cycle, SetMode, overlay, and save still run. |
| `--no-overlay` | Send no `E1` at all, leaving any existing overlay exactly as it is. |
| `--clear-overlay` | Send `E1` with every widget disabled, which actively removes the overlay. |
| `--no-save` | Do not persist to panel NVRAM. |

With no flags it runs a full recovery into Faith 1 with the GPU temp and TGP
overlay, and saves to NVRAM.

## Causes already fixed in code

Check these before assuming a new bug, because a blank panel that traces back to
one of them is a regression:

- Both the LCD and RGB I2C buses are pinned to 400 kHz (#21, #22). They share one
  physical engine, and a default-speed RGB write wedges that engine and blanks
  the panel.
- The sensor feed pushes `E3` at a fixed 1 Hz keep-alive (#20), otherwise the
  overlay widgets freeze.
- Transient NVAPI status `-1` is retried on writes (#17). Reads are deliberately
  not retried by `RetryingI2cBus`, so the tool absorbs flaky reads itself.
