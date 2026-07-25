using System.Text.Json.Serialization;

namespace AorusLcd.Gui.Models;

/// <summary>Minimal GitHub Releases API shape: only the fields the updater needs to pick a version and its installer asset.</summary>
public sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string? TagName { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("draft")]
    public bool Draft { get; set; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }

    [JsonPropertyName("assets")]
    public GitHubAsset[]? Assets { get; set; }
}

/// <summary>A downloadable file attached to a GitHub release.</summary>
public sealed class GitHubAsset
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("browser_download_url")]
    public string? BrowserDownloadUrl { get; set; }
}

/// <summary>Source-generated JSON context so release parsing stays trim/analyzer clean.</summary>
[JsonSerializable(typeof(GitHubRelease[]))]
internal sealed partial class GitHubReleaseJson : JsonSerializerContext;
