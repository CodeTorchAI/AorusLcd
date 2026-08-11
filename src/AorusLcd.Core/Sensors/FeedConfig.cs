using System.Text.Json;
using System.Text.Json.Serialization;

namespace AorusLcd.Core.Sensors;

/// <summary>Machine-wide ProgramData JSON config shared by GUI and service for the background sensor feed.</summary>
public sealed record FeedConfig
{
    /// <summary>Dashboard elements to display (bitmask of <see cref="LcdDisplayElements"/>).</summary>
    public uint DisplayElements { get; init; }

    /// <summary>Rotation interval in seconds for the built-in dashboard.</summary>
    public int IntervalSeconds { get; init; } = 4;

    /// <summary>Whether the background feed is enabled.</summary>
    public bool Enabled { get; init; }

    public LcdDisplayElements Elements => (LcdDisplayElements)DisplayElements;

    /// <summary>Machine-wide config path: <c>%ProgramData%\AorusLcd\feed.json</c>.</summary>
    /// <remarks>
    /// This file is intentionally writable by local users so the unelevated GUI can drive the
    /// dashboard. The LocalSystem service therefore treats its contents as untrusted numeric
    /// input and throttles reloads (see <see cref="AorusLcd.Core.ReloadThrottle"/>).
    /// </remarks>
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "AorusLcd", "feed.json");

    /// <summary>Load the config, or return a disabled default if absent/unreadable.</summary>
    public static async Task<FeedConfig> LoadAsync(string? path = null,
        CancellationToken cancellationToken = default)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                return JsonSerializer.Deserialize(json, FeedConfigJson.Default.FeedConfig) ?? new FeedConfig();
            }
        }
        catch (Exception)
        {
            // fall through to default
        }
        return new FeedConfig();
    }

    /// <summary>Persist via same-directory temp file and atomic replace so watchers never see half-written disabled-looking JSON.</summary>
    public async Task SaveAsync(string? path = null, CancellationToken cancellationToken = default)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(this, FeedConfigJson.Default.FeedConfig);

        string temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, json, cancellationToken).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>Source-generated JSON context so serialization works under NativeAOT.</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(FeedConfig))]
internal sealed partial class FeedConfigJson : JsonSerializerContext;
