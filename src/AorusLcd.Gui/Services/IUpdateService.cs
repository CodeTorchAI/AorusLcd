using System;
using System.Threading;
using System.Threading.Tasks;

namespace AorusLcd.Gui.Services;

/// <summary>Checks GitHub Releases for updates and downloads the installer; injectable seam for the view model.</summary>
public interface IUpdateService
{
    /// <summary>The running app's version (three-part, suffix stripped).</summary>
    Version CurrentVersion { get; }

    Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken cancellationToken = default);

    Task<string> DownloadSetupAsync(UpdateInfo update, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>Launch a downloaded installer (elevating via its manifest); the app then exits so
    /// the installer can replace files. Behind the interface so the update flow is testable.</summary>
    void LaunchInstaller(string setupPath);
}
