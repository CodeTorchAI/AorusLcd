using System;
using System.Threading;
using System.Threading.Tasks;
using AorusLcd.Core;

namespace AorusLcd.Gui.Services;

/// <summary>
/// Repairs the panel with exactly one writer on the bus. GIGABYTE Control Center's LCD service
/// drives the same controller and knows nothing about <see cref="Core.SystemBusLock"/>, so it is
/// stopped for the duration and started again afterwards.
/// </summary>
public sealed class PanelRecoveryCoordinator(IPanelRecovery hardware, IServiceControl services)
    : IPanelRecoveryCoordinator
{
    // Send no E1 and leave NVRAM alone: that is the combination that has actually relit a
    // dark panel, and it keeps GCC's stored configuration intact. See docs/RECOVERY.md.
    private static readonly RecoveryOptions Options = new()
    {
        Overlay = OverlayAction.Leave,
        Save = false,
    };

    private volatile bool _owesVendorServiceRestart;

    /// <summary>True while a run has GIGABYTE Control Center's LCD service stopped and still owes it a restart.</summary>
    public bool OwesVendorServiceRestart => _owesVendorServiceRestart;

    public async Task<RecoveryOutcome> RecoverAsync(IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        bool stoppedByThisRun = false;
        // Transitioning counts as occupied: a service on its way up or down still owns the bus.
        if (services.GetGccServiceState() is ServiceState.Running or ServiceState.Transitioning)
        {
            progress?.Report("Stopping GIGABYTE Control Center's LCD service...");
            try
            {
                await services.StopGccServiceAsync().ConfigureAwait(false);
                stoppedByThisRun = true;
            }
            catch (Exception e)
            {
                // The stop can land and still report failure, so trust the observed state over
                // the exit code. Owning the restart matters more than why the call complained.
                if (services.GetGccServiceState() != ServiceState.Stopped)
                {
                    return RecoveryOutcome.Skipped(
                        "GIGABYTE Control Center's LCD service could not be stopped, and two " +
                        $"processes must not write to the panel at once. {e.Message}");
                }
                stoppedByThisRun = true;
            }
        }
        _owesVendorServiceRestart = stoppedByThisRun;

        string? failure = null;
        try
        {
            await hardware.RecoverPanelAsync(Options, progress, ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            failure = e.Message;
        }

        // Always attempt the restart, so a failed repair cannot strand the vendor service.
        bool leftStopped = stoppedByThisRun && !await TryStartVendorServiceAsync(progress).ConfigureAwait(false);
        _owesVendorServiceRestart = false;
        return failure is null
            ? RecoveryOutcome.Succeeded(leftStopped)
            : RecoveryOutcome.Failed(failure, leftStopped);
    }

    private async Task<bool> TryStartVendorServiceAsync(IProgress<string>? progress)
    {
        progress?.Report("Restarting GIGABYTE Control Center's LCD service...");
        try
        {
            await services.StartGccServiceAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            // Best effort: the caller reports the stranded service rather than failing the repair.
            return false;
        }
    }
}
