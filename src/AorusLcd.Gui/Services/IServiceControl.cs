using System.Threading.Tasks;

namespace AorusLcd.Gui.Services;

/// <summary>Queries and controls the background feed service; injectable seam for the view model.</summary>
public interface IServiceControl
{
    /// <summary>Current service state (safe to call unelevated; never throws).</summary>
    ServiceState GetState();

    Task InstallAsync();

    Task UninstallAsync();

    Task StartAsync();

    Task StopAsync();
}
