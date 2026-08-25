namespace AorusLcd.Gui.Services;

/// <summary>Result of a recovery attempt, including what it left GIGABYTE Control Center's LCD service in.</summary>
/// <param name="Recovered">True when the repaint sequence ran to completion.</param>
/// <param name="VendorServiceLeftStopped">True when this run stopped GCC's LCD service and could not start it again.</param>
/// <param name="Error">Why the attempt did not recover the panel, as a complete sentence.</param>
public sealed record RecoveryOutcome(bool Recovered, bool VendorServiceLeftStopped, string? Error)
{
    /// <summary>The bus could not be cleared, so nothing was written to the panel.</summary>
    public static RecoveryOutcome Skipped(string error) => new(false, false, $"Recovery skipped: {error}");

    public static RecoveryOutcome Failed(string error, bool vendorServiceLeftStopped)
        => new(false, vendorServiceLeftStopped, $"Recovery failed: {error}");

    public static RecoveryOutcome Succeeded(bool vendorServiceLeftStopped)
        => new(true, vendorServiceLeftStopped, null);

    /// <summary>One line for the status bar, warning about a stopped vendor service whether or not the repair worked.</summary>
    public string ToStatusMessage()
    {
        string message = Recovered
            ? "Panel recovered. If it is still dark, see docs/RECOVERY.md."
            : Error ?? "Recovery did not run.";
        return VendorServiceLeftStopped
            ? $"{message} GIGABYTE Control Center's LCD service is still stopped; start it from Services."
            : message;
    }
}
