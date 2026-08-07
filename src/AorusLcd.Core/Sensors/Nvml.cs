using System.Runtime.InteropServices;

namespace AorusLcd.Core.Sensors;

/// <summary>Minimal cross-OS NVML P/Invoke layer for documented driver telemetry used by the panel sensor feed.</summary>
internal static class Nvml
{
    private const string Lib = "nvml";

    static Nvml()
    {
        NativeLibrary.SetDllImportResolver(typeof(Nvml).Assembly, (name, assembly, path) =>
        {
            if (name != Lib)
            {
                return IntPtr.Zero;
            }
            foreach (var candidate in Candidates())
            {
                if (NativeLibrary.TryLoad(candidate, out var handle))
                {
                    return handle;
                }
            }
            return IntPtr.Zero;
        });
    }

    private static IEnumerable<string> Candidates()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return "nvml.dll";
        }
        else
        {
            yield return "libnvidia-ml.so.1";
            yield return "libnvidia-ml.so";
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Utilization
    {
        public uint Gpu;
        public uint Memory;
    }

    // nvmlPciInfo_t: busIdLegacy[16], domain, bus, device, pciDeviceId,
    // pciSubSystemId, busId[32]. Only domain/bus/device are needed for matching.
    [StructLayout(LayoutKind.Sequential)]
    public struct PciInfo
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] BusIdLegacy;
        public uint Domain;
        public uint Bus;
        public uint Device;
        public uint PciDeviceId;
        public uint PciSubSystemId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] BusId;
    }

    // nvmlFanSpeedInfo_t: { version, fan (in), speed (out, RPM) }.
    // version = NVML_STRUCT_VERSION(FanSpeedInfo, 1) = sizeof(struct) | (1 << 24).
    [StructLayout(LayoutKind.Sequential)]
    public struct FanSpeedInfo
    {
        public uint Version;
        public uint Fan;
        public uint Speed;
    }

    public const uint FanSpeedInfoV1 = 12u | (1u << 24);

    // nvmlFieldValue_t: fieldId, scopeId, timestamp, latencyUsec, valueType,
    // nvmlReturn, then an 8-byte value union (uiVal is the low 32 bits on LE).
    [StructLayout(LayoutKind.Sequential)]
    public struct FieldValue
    {
        public uint FieldId;
        public uint ScopeId;
        public long Timestamp;
        public long LatencyUsec;
        public uint ValueType;
        public int NvmlReturn;
        public ulong ValueRaw;
    }

    // NVML_FI_DEV_POWER_INSTANT: current GPU power in mW, all architectures.
    // nvmlDeviceGetPowerUsage is deprecated and returns a stuck value on Blackwell.
    public const uint FiDevPowerInstant = 186u;

    // 0 = NVML_SUCCESS. sensorType 0 = GPU; clockType 0 = graphics, 2 = mem.
    [DllImport(Lib, EntryPoint = "nvmlInit_v2")]
    public static extern int Init();

    [DllImport(Lib, EntryPoint = "nvmlShutdown")]
    public static extern int Shutdown();

    [DllImport(Lib, EntryPoint = "nvmlDeviceGetCount_v2")]
    public static extern int GetCount(out uint count);

    [DllImport(Lib, EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
    public static extern int GetHandleByIndex(uint index, out IntPtr device);

    [DllImport(Lib, EntryPoint = "nvmlDeviceGetPciInfo_v3")]
    public static extern int GetPciInfo(IntPtr device, ref PciInfo pci);

    [DllImport(Lib, EntryPoint = "nvmlDeviceGetTemperature")]
    public static extern int GetTemperature(IntPtr device, uint sensorType, out uint tempC);

    [DllImport(Lib, EntryPoint = "nvmlDeviceGetClockInfo")]
    public static extern int GetClockInfo(IntPtr device, uint clockType, out uint clockMhz);

    [DllImport(Lib, EntryPoint = "nvmlDeviceGetUtilizationRates")]
    public static extern int GetUtilizationRates(IntPtr device, out Utilization util);

    [DllImport(Lib, EntryPoint = "nvmlDeviceGetFanSpeed")]
    public static extern int GetFanSpeed(IntPtr device, out uint speedPercent);

    // Actual fan RPM (per-fan), added in recent drivers. Absent on older ones,
    // so callers must handle EntryPointNotFoundException and fall back.
    [DllImport(Lib, EntryPoint = "nvmlDeviceGetFanSpeedRPM")]
    public static extern int GetFanSpeedRpm(IntPtr device, ref FanSpeedInfo fanSpeed);

    [DllImport(Lib, EntryPoint = "nvmlDeviceGetPowerUsage")]
    public static extern int GetPowerUsage(IntPtr device, out uint milliwatts);

    [DllImport(Lib, EntryPoint = "nvmlDeviceGetFieldValues")]
    public static extern int GetFieldValues(IntPtr device, int valuesCount, ref FieldValue values);
}
