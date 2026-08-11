using System.Collections.Generic;

namespace AorusLcd.Gui.Services;

/// <summary>Per-user GUI preferences (instance members); injectable seam for the view model. Loading is a static factory on the concrete type.</summary>
public interface ISettingsStore
{
    /// <summary>Whether the system-tray icon is shown. When false, closing the window exits the GUI.</summary>
    bool ShowTrayIcon { get; set; }

    /// <summary>Last-applied legacy RGB effect name; restored on launch (the card can't be read back).</summary>
    string LastRgbMode { get; set; }

    /// <summary>Last-applied Blackwell RGB effect name; restored on launch.</summary>
    string LastRgbBlackwellMode { get; set; }

    /// <summary>Last-applied RGB colour list as RRGGBB hex; the first entry is the primary colour.</summary>
    List<string> LastRgbColors { get; set; }

    /// <summary>Last-applied RGB brightness percent (0..100).</summary>
    int LastRgbBrightness { get; set; }

    /// <summary>Last-applied RGB speed step (0..5).</summary>
    int LastRgbSpeed { get; set; }

    /// <summary>Persist preferences, creating the directory if needed. Never throws.</summary>
    void Save(string? path = null);
}
