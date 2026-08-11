namespace AorusLcd.Core.Sensors;

/// <summary>Reads NVML sensors for E3: MHz clocks, percent usage/RAM, whole-watt TGP, fan value, FPS=0; refuses PCI bus mismatches and uses device 0 only when unknown.</summary>
public sealed class NvmlSensorSource : ISensorSource
{
    private const uint TemperatureGpu = 0;
    private const uint ClockGraphics = 0;
    private const uint ClockMemory = 2;

    private readonly IntPtr _device;
    private bool _initialized;
    private static bool _fanRpmUnavailable;
    private static bool _instantPowerUnavailable;

    public NvmlSensorSource(uint? pciBusId = null)
    {
        if (Nvml.Init() != 0)
        {
            throw new InvalidOperationException(
                "NVML initialization failed. Ensure the NVIDIA driver (nvml) is installed.");
        }
        _initialized = true;
        try
        {
            _device = ResolveDevice(pciBusId);
        }
        catch
        {
            Dispose(); // release the NVML init reference if device resolution fails
            throw;
        }
    }

    public SensorSample Read()
    {
        var device = _device;

        int temp = Nvml.GetTemperature(device, TemperatureGpu, out uint t) == 0 ? (int)t : 0;
        int gpuClock = Nvml.GetClockInfo(device, ClockGraphics, out uint gc) == 0 ? (int)gc : 0;
        int ramClock = Nvml.GetClockInfo(device, ClockMemory, out uint mc) == 0 ? (int)mc : 0;
        var util = Nvml.GetUtilizationRates(device, out var u) == 0 ? u : default;
        int fan = ReadFanRpm(device);
        int powerMw = ReadPowerMw(device);

        return new SensorSample
        {
            GpuTempC = temp,
            GpuClockMhz = gpuClock,
            GpuUsagePercent = (int)util.Gpu,
            FanSpeed = fan,
            RamClockMhz = ramClock,
            RamUsagePercent = (int)util.Memory,
            Fps = 0,
            TgpWatts = (powerMw + 999) / 1000, // mW -> W, rounded up; panel prints the raw number
        };
    }

    /// <summary>Read fan RPM via per-fan API when available; otherwise fall back to 0-100 percent without changing fan control.</summary>
    private static int ReadFanRpm(IntPtr device)
    {
        if (!_fanRpmUnavailable)
        {
            try
            {
                var info = new Nvml.FanSpeedInfo { Version = Nvml.FanSpeedInfoV1, Fan = 0 };
                if (Nvml.GetFanSpeedRpm(device, ref info) == 0)
                {
                    return (int)info.Speed;
                }
            }
            catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException)
            {
                _fanRpmUnavailable = true; // old driver: don't probe the missing export again
            }
        }

        return Nvml.GetFanSpeed(device, out uint pct) == 0 ? (int)pct : 0;
    }

    /// <summary>Read current power via the instant field (correct on Blackwell); fall back to the deprecated call, which reports a stuck value on RTX 50-series.</summary>
    private static int ReadPowerMw(IntPtr device)
    {
        if (!_instantPowerUnavailable)
        {
            try
            {
                var field = new Nvml.FieldValue { FieldId = Nvml.FiDevPowerInstant };
                if (Nvml.GetFieldValues(device, 1, ref field) == 0 && field.NvmlReturn == 0)
                {
                    return (int)(uint)field.ValueRaw;
                }
                // Transient or per-device failure: fall back this cycle but keep probing.
            }
            catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException)
            {
                _instantPowerUnavailable = true; // old driver without the export: stop probing
            }
        }

        return Nvml.GetPowerUsage(device, out uint mw) == 0 ? (int)mw : 0;
    }

    /// <summary>Pick the NVML device matching <paramref name="pciBusId"/>, or device 0 only when the bus id is unknown.</summary>
    private static IntPtr ResolveDevice(uint? pciBusId)
    {
        if (pciBusId is uint wanted)
        {
            int status = Nvml.GetCount(out uint count);
            if (status != 0)
            {
                // Distinguish an enumeration failure from a genuine no-match so the log is not misleading.
                throw new InvalidOperationException(
                    $"NVML: could not enumerate GPUs (nvmlDeviceGetCount status {status}).");
            }
            for (uint i = 0; i < count; i++)
            {
                if (Nvml.GetHandleByIndex(i, out var device) != 0)
                {
                    continue;
                }
                var pci = new Nvml.PciInfo { BusIdLegacy = new byte[16], BusId = new byte[32] };
                if (Nvml.GetPciInfo(device, ref pci) == 0 && pci.Bus == wanted)
                {
                    return device;
                }
            }

            throw new InvalidOperationException(
                $"NVML: no GPU matched PCI bus 0x{wanted:X2} among {count} devices; refusing to guess.");
        }

        if (Nvml.GetHandleByIndex(0, out var first) != 0)
        {
            throw new InvalidOperationException("NVML could not open any GPU (index 0).");
        }
        return first;
    }

    public void Dispose()
    {
        if (_initialized)
        {
            // Best-effort teardown; the status is intentionally discarded
            // (Dispose has no logger and a shutdown failure isn't actionable).
            _ = Nvml.Shutdown();
            _initialized = false;
        }
    }
}
