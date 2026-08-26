using System;
using System.Threading;
using System.Threading.Tasks;

namespace AorusLcd.Gui.Services;

/// <summary>Repairs a blank or frozen panel with the I2C bus cleared of other writers.</summary>
public interface IPanelRecoveryCoordinator
{
    /// <summary>True while a run has GIGABYTE Control Center's LCD service stopped and still owes it a restart.</summary>
    bool OwesVendorServiceRestart { get; }

    Task<RecoveryOutcome> RecoverAsync(IProgress<string>? progress = null, CancellationToken ct = default);
}
