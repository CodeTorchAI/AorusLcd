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
public sealed class ServiceControl : IServiceControl
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
        string dir = Path.GetDirectoryName(InstalledExePath)!; // %ProgramData%\AorusLcd\bin
        string dataDir = Path.GetDirectoryName(dir)!;          // %ProgramData%\AorusLcd

        // SECURITY: bin\ holds a LocalSystem service exe, so a standard user must not be able to
        // own, replace, or delete it. %ProgramData% lets any user create child folders and become
        // their owner with inherited Full Control, so a pre-created AorusLcd\ or bin\ would leave
        // the installed exe user-writable - a local privilege escalation to LocalSystem. The
        // elevated batch therefore, before copying the exe (all &&-chained so any failure blocks
        // the copy and service registration - fail-closed):
        //   1. refuses a pre-planted reparse point (junction) at either dir, so the privileged
        //      copy/icacls can't be redirected to an attacker-controlled target;
        //   2. reclaims ownership of the whole tree;
        //   3. rebuilds each DACL from scratch - /reset drops every attacker-added explicit ACE,
        //      /inheritance:r freezes the result, CREATOR OWNER is removed, then an exact allow-list
        //      is granted - so foreign ACEs can't survive:
        //        bin\      : SYSTEM/Administrators Full, Users Read+Execute only.
        //        AorusLcd\ : SYSTEM/Administrators Full; Users may traverse and create
        //                    feed.json/service.log (RX,W - no Delete/Delete-child, so bin\ can't be
        //                    removed) with Modify on files directly in the dir only.
        // The copied exe then inherits only bin\'s locked-down ACL. feed.json stays user-writable on
        // purpose so the unelevated GUI can drive the dashboard; the service treats its contents as
        // untrusted numeric input and throttles reloads, so do NOT tighten the file ACE.
        // Residual risk: the reparse rejection is a check-then-use guard. Because bin\ is still
        // user-owned until the icacls steps below lock it, a local attacker could in theory win a
        // sub-millisecond race and swap bin\ for a junction between the check and the copy. Fully
        // closing that needs an elevated helper operating on directory handles opened with
        // FILE_FLAG_OPEN_REPARSE_POINT; the pre-planted (non-racing) case is closed here.
        const string system = "*S-1-5-18";
        const string administrators = "*S-1-5-32-544";
        const string users = "*S-1-5-32-545";
        const string creatorOwner = "*S-1-3-0";
        string fullControl = $"\"{system}:(OI)(CI)F\" \"{administrators}:(OI)(CI)F\"";
        // Fully qualified so a planted icacls.exe/fsutil.exe/sc.exe on PATH or in the working
        // directory can't hijack the elevated batch (copy/mkdir/exit are cmd built-ins).
        string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string fsutil = $"\"{sys}\\fsutil.exe\"";
        string icacls = $"\"{sys}\\icacls.exe\"";
        string sc = $"\"{sys}\\sc.exe\"";
        string batch =
            $"{fsutil} reparsepoint query \"{dataDir}\" >nul 2>nul && exit 1 & " +
            $"{fsutil} reparsepoint query \"{dir}\" >nul 2>nul && exit 1 & " +
            $"mkdir \"{dir}\" 2>nul & " +
            $"{icacls} \"{dataDir}\" /setowner {system} /T /C && " +
            $"{icacls} \"{dataDir}\" /reset && " +
            $"{icacls} \"{dataDir}\" /inheritance:r && " +
            $"{icacls} \"{dataDir}\" /remove:g {creatorOwner} && " +
            $"{icacls} \"{dataDir}\" /grant:r {fullControl} \"{users}:(RX,W)\" && " +
            $"{icacls} \"{dataDir}\" /grant \"{users}:(OI)(NP)(IO)M\" && " +
            $"{icacls} \"{dir}\" /reset && " +
            $"{icacls} \"{dir}\" /inheritance:r && " +
            $"{icacls} \"{dir}\" /remove:g {creatorOwner} && " +
            $"{icacls} \"{dir}\" /grant:r {fullControl} \"{users}:(OI)(CI)RX\" && " +
            $"copy /y \"{source}\" \"{InstalledExePath}\" && " +
            $"{sc} create {ServiceName} binPath= \"{InstalledExePath}\" start= auto DisplayName= \"AorusLcd Sensor Feed\" && " +
            $"{sc} description {ServiceName} \"Pushes live GPU sensor data (temp, clocks, usage, fan, TGP) to the Aorus LCD Edge View dashboard for AorusLcd. Safe to stop if you do not use the live dashboard.\" && " +
            $"{sc} failure {ServiceName} reset= 86400 actions= restart/5000/restart/10000/restart/60000 && " +
            $"{sc} start {ServiceName}";
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
