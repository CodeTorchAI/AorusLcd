using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.ServiceProcess;
using System.Threading.Tasks;

namespace AorusLcd.Gui.Services;

/// <summary>Installed/running state of the background feed service.</summary>
public enum ServiceState
{
    Unsupported,
    NotInstalled,
    Stopped,
    Running,
    Transitioning,
}

/// <summary>Manages unelevated service state queries and elevated install/uninstall/start/stop for the NativeAOT feed service.</summary>
[SupportedOSPlatform("windows")]
public sealed class ServiceControl
{
    public const string ServiceName = "AorusLcdFeed";

    /// <summary>Where the service exe is copied to and run from once installed.</summary>
    public static string InstalledExePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "AorusLcd", "bin", "AorusLcd.Service.exe");

    /// <summary>Current service state (safe to call unelevated; never throws).</summary>
    public ServiceState GetState()
    {
        if (!OperatingSystem.IsWindows())
        {
            return ServiceState.Unsupported;
        }
        try
        {
            using var sc = new ServiceController(ServiceName);
            return sc.Status switch
            {
                ServiceControllerStatus.Running => ServiceState.Running,
                ServiceControllerStatus.Stopped => ServiceState.Stopped,
                _ => ServiceState.Transitioning,
            };
        }
        catch (InvalidOperationException)
        {
            return ServiceState.NotInstalled; // no such service
        }
    }

    /// <summary>Find bundled service exe next to the GUI or under <c>service\</c>; null when not shipped with the app.</summary>
    public static string? FindBundledServiceExe()
    {
        string baseDir = AppContext.BaseDirectory;
        string[] candidates =
        [
            Path.Combine(baseDir, "AorusLcd.Service.exe"),
            Path.Combine(baseDir, "service", "AorusLcd.Service.exe"),
        ];
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    /// <summary>Copy bundled exe into place and register/start the service in one elevated batch; throws if missing.</summary>
    public Task InstallAsync()
    {
        var source = FindBundledServiceExe()
            ?? throw new FileNotFoundException(
                "AorusLcd.Service.exe was not found next to the app. Publish the service and place it beside the GUI.");
        string dir = Path.GetDirectoryName(InstalledExePath)!;
        string dataDir = Path.GetDirectoryName(dir)!; // %ProgramData%\AorusLcd
        // Elevated batch chains critical steps with &&; mkdir may already exist, so it uses & and failures still surface.
        // SECURITY: the LocalSystem service exe lives in bin\, so users must not be
        // able to overwrite it or delete bin\. Grant Users create-file+write on the
        // data dir itself (RX,W - no Delete/Delete-child, so bin\ can't be removed)
        // and Modify inherit-only + no-propagate on files, so only files directly in
        // the dir (feed.json/service.log) are user-writable - never bin\ or its exe.
        // Verified via icacls: bin\AorusLcd.Service.exe gets no Users ACE.
        // feed.json is deliberately user-writable so the unelevated GUI can drive the
        // dashboard; the service treats its contents as untrusted numeric input and
        // throttles reloads, so do NOT tighten these ACLs to lock users out of feed.json.
        string userSid = "*S-1-5-32-545";
        string batch =
            $"mkdir \"{dir}\" 2>nul & copy /y \"{source}\" \"{InstalledExePath}\" && " +
            $"icacls \"{dataDir}\" /grant \"{userSid}:(RX,W)\" /grant \"{userSid}:(OI)(NP)(IO)M\" && " +
            $"sc create {ServiceName} binPath= \"{InstalledExePath}\" start= auto DisplayName= \"AorusLcd Sensor Feed\" && " +
            $"sc description {ServiceName} \"Pushes live GPU sensor data (temp, clocks, usage, fan, TGP) to the Aorus LCD Edge View dashboard for AorusLcd. Safe to stop if you do not use the live dashboard.\" && " +
            $"sc failure {ServiceName} reset= 86400 actions= restart/5000/restart/10000/restart/60000 && " +
            $"sc start {ServiceName}";
        return RunElevatedCmdAsync(batch);
    }

    public Task UninstallAsync()
        => RunElevatedCmdAsync($"sc stop {ServiceName} & sc delete {ServiceName}");

    public Task StartAsync() => RunElevatedCmdAsync($"sc start {ServiceName}");

    public Task StopAsync() => RunElevatedCmdAsync($"sc stop {ServiceName}");

    private static async Task RunElevatedCmdAsync(string batch)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c {batch}")
        {
            UseShellExecute = true, // required for the runas verb (UAC elevation)
            Verb = "runas",
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to launch the elevated helper.");
        await process.WaitForExitAsync().ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"The service operation exited with code {process.ExitCode}.");
        }
    }
}
