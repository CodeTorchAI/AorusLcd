using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AorusLcd.Gui.Models;

namespace AorusLcd.Gui.Services;

/// <summary>A newer release found on GitHub, with the installer asset to download.</summary>
public sealed record UpdateInfo(Version Version, string TagName, string ReleaseUrl, string SetupUrl, string SetupName);

/// <summary>Checks GitHub Releases for a newer version, downloads its setup.exe, and launches the installer to self-update.</summary>
public sealed class UpdateService : IUpdateService
{
    private const string ReleasesApi = "https://api.github.com/repos/JustinMDotNet/AorusLcd/releases?per_page=20";

    private static readonly HttpClient Http = CreateClient();

    /// <summary>The running app's version (three-part, suffix stripped).</summary>
    public Version CurrentVersion { get; } =
        NormalizeVersion(Assembly.GetEntryAssembly()?.GetName().Version) ?? new Version(0, 0, 0);

    /// <summary>Query GitHub for the newest release that ships an installer; return it only if it is newer than the running version.</summary>
    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        var releases = await Http.GetFromJsonAsync(
            ReleasesApi,
            GitHubReleaseJson.Default.GitHubReleaseArray,
            cancellationToken).ConfigureAwait(false);
        if (releases is null)
        {
            return null;
        }

        UpdateInfo? newest = null;
        bool newestIsPrerelease = false;
        foreach (var release in releases)
        {
            if (release.Draft || TryParseTag(release.TagName) is not Version version)
            {
                continue;
            }
            var asset = release.Assets?.FirstOrDefault(a =>
                a.Name is not null && a.Name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase));
            if (asset?.BrowserDownloadUrl is null || asset.Name is null)
            {
                continue;
            }
            // Higher numeric version wins; for the same version, a stable release beats a prerelease.
            bool better = newest is null
                || version > newest.Version
                || (version == newest.Version && newestIsPrerelease && !release.Prerelease);
            if (better)
            {
                newest = new UpdateInfo(version, release.TagName ?? version.ToString(),
                    release.HtmlUrl ?? "", asset.BrowserDownloadUrl, asset.Name);
                newestIsPrerelease = release.Prerelease;
            }
        }

        return newest is not null && newest.Version > CurrentVersion ? newest : null;
    }

    /// <summary>Stream the installer to a temp file, reporting 0..1 progress; the caller then launches it.</summary>
    public async Task<string> DownloadSetupAsync(UpdateInfo update, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // Fresh per-download directory so the path isn't predictable and CreateNew can never
        // collide with (or clobber) a leftover file from an earlier run.
        var dir = Path.Combine(Path.GetTempPath(), "AorusLcdUpdate", Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        // The asset name is remote input; reduce it to a bare filename so it can't escape the folder.
        var fileName = Path.GetFileName(update.SetupName);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "AorusLcd-setup.exe";
        }
        var path = Path.Combine(dir, fileName);

        try
        {
            using var response = await Http.GetAsync(update.SetupUrl,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            long? total = response.Content.Headers.ContentLength;

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                long readTotal = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    readTotal += read;
                    if (total is > 0)
                    {
                        progress?.Report((double)readTotal / total.Value);
                    }
                }
            }
            return path;
        }
        catch
        {
            // Don't leave a half-written installer behind for the next run to trip over.
            TryDeleteDirectory(dir);
            throw;
        }
    }

    private static void TryDeleteDirectory(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception)
        {
            // best-effort cleanup
        }
    }

    /// <summary>Launch the installer via ShellExecute so its admin manifest triggers the UAC prompt; the app then exits to let it replace files.</summary>
    public void LaunchInstaller(string setupPath)
        => Process.Start(new ProcessStartInfo { FileName = setupPath, UseShellExecute = true });

    /// <summary>Parse a release tag like <c>v1.2.3</c> or <c>1.2.3-alpha</c> to its three-part numeric version, or null if unparseable.</summary>
    private static Version? TryParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }
        var core = tag.Trim().TrimStart('v', 'V');
        int cut = core.IndexOfAny(['-', '+']); // drop pre-release / build metadata
        if (cut >= 0)
        {
            core = core[..cut];
        }
        return NormalizeVersion(Version.TryParse(core, out var parsed) ? parsed : null);
    }

    /// <summary>Reduce any Version to a comparable three-part value (Major.Minor.Build, no revision).</summary>
    private static Version? NormalizeVersion(Version? version)
        => version is null ? null : new Version(version.Major, version.Minor, Math.Max(version.Build, 0));

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        // GitHub rejects requests without a User-Agent; Accept pins the stable v3 media type.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AorusLcd-Updater");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
}
