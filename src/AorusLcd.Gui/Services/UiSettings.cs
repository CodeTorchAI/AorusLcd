using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AorusLcd.Gui.Services;

/// <summary>Per-user GUI preferences (tray visibility, etc.), persisted as JSON under the user's roaming app data.</summary>
public sealed class UiSettings : ISettingsStore
{
    /// <summary>Whether the system-tray icon is shown. When false, closing the window exits the GUI.</summary>
    public bool ShowTrayIcon { get; set; } = true;

    /// <summary>Last-applied legacy RGB effect name; restored on launch (the card can't be read back).</summary>
    public string LastRgbMode { get; set; } = "Static";

    /// <summary>Last-applied Blackwell RGB effect name; restored on launch.</summary>
    public string LastRgbBlackwellMode { get; set; } = "Static";

    /// <summary>Last-applied RGB colour list as RRGGBB hex; the first entry is the primary colour.</summary>
    public List<string> LastRgbColors { get; set; } = ["FF6600"];

    /// <summary>Last-applied RGB brightness percent (0..100).</summary>
    public int LastRgbBrightness { get; set; } = 100;

    /// <summary>Last-applied RGB speed step (0..5).</summary>
    public int LastRgbSpeed { get; set; } = 2;

    private static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AorusLcd", "ui.json");

    /// <summary>Load preferences, returning defaults if the file is absent or unreadable.</summary>
    public static UiSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize(json, UiSettingsJson.Default.UiSettings) ?? new UiSettings();
            }
        }
        catch (Exception)
        {
            // fall through to defaults
        }
        return new UiSettings();
    }

    /// <summary>Persist preferences, creating the directory if needed. Never throws.</summary>
    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(path, JsonSerializer.Serialize(this, UiSettingsJson.Default.UiSettings));
        }
        catch (Exception)
        {
            // preferences are best-effort; a failed save must not crash the UI
        }
    }
}

/// <summary>Source-generated JSON context so serialization stays trim/analyzer clean.</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(UiSettings))]
internal sealed partial class UiSettingsJson : JsonSerializerContext;
