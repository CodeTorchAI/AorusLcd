using System.Threading.Tasks;

namespace AorusLcd.Gui.Services;

/// <summary>Queries and controls the background feed service; injectable seam for the view model.</summary>
public interface IServiceControl
{
    /// <summary>Current service state (safe to call unelevated; never throws).</summary>
    ServiceState GetState();

    /// <summary>State of GIGABYTE Control Center's LCD service, which drives the same I2C bus and ignores our bus lock.</summary>
    ServiceState GetGccServiceState();

    Task InstallAsync();

    Task UninstallAsync();

    Task StartAsync();

    Task StopAsync();

    /// <summary>Stop GIGABYTE Control Center's LCD service and wait for it to actually stop.</summary>
    Task StopGccServiceAsync();

    Task StartGccServiceAsync();
}
