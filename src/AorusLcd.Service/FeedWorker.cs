using System.Runtime.Versioning;
using AorusLcd.Core;
using AorusLcd.Core.Nvapi;
using AorusLcd.Core.Sensors;
using Microsoft.Extensions.Hosting;

namespace AorusLcd.Service;

/// <summary>Background service worker that watches <see cref="FeedConfig"/> and runs the shared E1/E3 sensor feed through NVAPI.</summary>
[SupportedOSPlatform("windows")]
public sealed class FeedWorker : BackgroundService
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "AorusLcd", "service.log");

    private const int InitialRetryMs = 2000;
    private const int MaxRetryMs = 60000;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Log("service starting");
        int retryMs = InitialRetryMs;
        while (!stoppingToken.IsCancellationRequested)
        {
            var config = await FeedConfig.LoadAsync(cancellationToken: stoppingToken).ConfigureAwait(false);
            if (!config.Enabled || config.Elements == LcdDisplayElements.None)
            {
                retryMs = InitialRetryMs;
                await WaitForConfigChangeAsync(stoppingToken);
                continue;
            }

            try
            {
                if (await RunFeedAsync(config, stoppingToken))
                {
                    retryMs = InitialRetryMs; // ran until a config change; reset backoff
                }
                else
                {
                    // Hardware may be unavailable at boot; retry with bounded backoff but wake on config changes.
                    Log($"no Aorus LCD found; retrying in {retryMs}ms");
                    await WaitForConfigChangeOrDelayAsync(retryMs, stoppingToken);
                    retryMs = Math.Min(retryMs * 2, MaxRetryMs);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception e)
            {
                Log($"feed error: {e.Message}; retrying in 10s");
                await DelaySafe(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
        Log("service stopping");
    }

    /// <summary>Run current config until it changes or service stops; false means no panel found, so caller backs off.</summary>
    private async Task<bool> RunFeedAsync(FeedConfig config, CancellationToken stoppingToken)
    {
        using var busLock = new SystemBusLock();

        // Probing the bus is itself a write/read, so it must hold the shared lock
        // or it can interleave with (and corrupt) a concurrent GUI upload.
        (NvApiI2cBus Bus, string GpuName)? located;
        using (busLock.Acquire())
        {
            located = NvApiPanelLocator.Locate();
        }
        if (located is null)
        {
            return false;
        }

        using var reconfigured = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using var watcher = WatchConfig(() => reconfigured.Cancel());

        using var sensors = new NvmlSensorSource(located.Value.Bus.PciBusId);
        var panel = new PanelController(new RetryingI2cBus(located.Value.Bus));
        int pollMs = SensorFeedTiming.KeepAlivePollMs;

        Log($"feeding {config.Elements} interval {config.IntervalSeconds}s poll {pollMs}ms on {located.Value.GpuName}");
        var loop = new SensorFeedLoop(panel, sensors, () => busLock.Acquire());
        try
        {
            await loop.RunAsync(config.Elements, config.IntervalSeconds, pollMs, reconfigured.Token);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            Log("config changed; reloading");
        }
        return true;
    }

    private async Task WaitForConfigChangeAsync(CancellationToken stoppingToken)
    {
        using var changed = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using var watcher = WatchConfig(() => changed.Cancel());
        await DelaySafe(Timeout.InfiniteTimeSpan, changed.Token);
    }

    /// <summary>Wait up to <paramref name="delayMs"/>, waking early on a config change.</summary>
    private async Task WaitForConfigChangeOrDelayAsync(int delayMs, CancellationToken stoppingToken)
    {
        using var changed = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using var watcher = WatchConfig(() => changed.Cancel());
        await DelaySafe(TimeSpan.FromMilliseconds(delayMs), changed.Token);
    }

    private static FileSystemWatcher? WatchConfig(Action onChanged)
    {
        var dir = Path.GetDirectoryName(FeedConfig.DefaultPath)!;
        Directory.CreateDirectory(dir);
        var watcher = new FileSystemWatcher(dir, Path.GetFileName(FeedConfig.DefaultPath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        watcher.Changed += (_, _) => onChanged();
        watcher.Created += (_, _) => onChanged();
        watcher.Deleted += (_, _) => onChanged();
        watcher.Renamed += (_, _) => onChanged();
        return watcher;
    }

    private static async Task DelaySafe(TimeSpan delay, CancellationToken token)
    {
        try
        {
            await Task.Delay(delay, token);
        }
        catch (OperationCanceledException)
        {
            // expected when the config changes or the service stops
        }
    }

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"{DateTime.Now:s}  {message}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // logging must never take down the service
        }
    }
}
