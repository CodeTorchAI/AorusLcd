using System;
using System.Threading;
using System.Threading.Tasks;
using AorusLcd.Core;

namespace AorusLcd.Gui.Services;

/// <summary>The hardware half of a panel repair, split from <see cref="IHardwareService"/> so a caller that only recovers needs nothing else.</summary>
public interface IPanelRecovery
{
    /// <summary>Run the docs/RECOVERY.md repaint sequence against a blank or frozen panel.</summary>
    Task RecoverPanelAsync(RecoveryOptions options, IProgress<string>? progress = null,
        CancellationToken ct = default);
}
