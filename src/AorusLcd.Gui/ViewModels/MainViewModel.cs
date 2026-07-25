using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using AorusLcd.Core;
using AorusLcd.Core.Nvapi;
using AorusLcd.Core.Rgb;
using AorusLcd.Core.Sensors;
using AorusLcd.Gui.Models;
using AorusLcd.Gui.Services;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AorusLcd.Gui.ViewModels;

/// <summary>Main UI state for status, uploads, sensor dashboard, and RGB; service-backed sensor feed keeps running after GUI close.</summary>
public partial class MainViewModel : ViewModelBase
{
    private readonly HardwareService _hw = new();
    private readonly ServiceControl _service = new();
    private readonly UpdateService _update = new();
    private readonly UiSettings _uiSettings = UiSettings.Load();

    private UpdateInfo? _pendingUpdate;

    /// <summary>Raised when the app should exit (e.g. after launching a downloaded update installer). Wired by <c>App</c>.</summary>
    public event EventHandler? ExitRequested;

    /// <summary>Set by the view to present a file picker (needs a TopLevel).</summary>
    public Func<Task<string?>>? ImagePicker { get; set; }

    public MainViewModel()
    {
        RgbModes = ["Static", "Breathing", "Color Cycle", "Flash", "Wave",
            "Gradient", "Color Shift", "Dual Flash", "Tricolor"];
        SelectedRgbMode = RgbModes[0];
        RgbBlackwellModes = ["Static", "Direct", "Pulse", "Flash", "Double Flash",
            "Color Cycle", "Wave", "Gradient", "Color Shift", "Dazzle"];
        SelectedRgbBlackwellMode = RgbBlackwellModes[0];
        RgbColorList.CollectionChanged += OnRgbColorListChanged;
        RgbColorList[0].PropertyChanged += OnColorItemChanged;
        RestoreRgbSettings();
        StartWithWindows = StartupService.IsEnabled(); // reflect current registry state
        ShowTrayIcon = _uiSettings.ShowTrayIcon;
        RefreshServiceState();
        StatusMessage = _hw.IsSupportedPlatform
            ? "Connecting to the panel…"
            : "Hardware control needs Windows (NVAPI) for now; UI is cross-platform.";
        UpdatePreview();
    }

    /// <summary>Auto-connect to the panel on startup (Windows only) so the user doesn't have to click Refresh.</summary>
    public Task AutoConnectAsync()
        => _hw.IsSupportedPlatform ? RefreshStatusCommand.ExecuteAsync(null) : Task.CompletedTask;

    /// <summary>Silently check for a newer release on startup; surfaces the banner only when an update is found.</summary>
    public Task CheckForUpdatesOnStartupAsync() => CheckForUpdatesCoreAsync(silent: true);

    // ---- global ------------------------------------------------------------

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    public partial bool IsBusy { get; set; }

    /// <summary>Inverse of <see cref="IsBusy"/>; action buttons bind their enabled state to this.</summary>
    public bool IsNotBusy => !IsBusy;

    // ---- app version / auto-update -----------------------------------------

    /// <summary>Human-readable running version for the About card.</summary>
    public string AppVersion => $"Version {_update.CurrentVersion.ToString(3)}";

    /// <summary>Auto-update targets the Windows installer, so the controls only apply on Windows.</summary>
    public bool UpdatesSupported => OperatingSystem.IsWindows();

    /// <summary>Status/result line for the update check (empty until the user checks or an update is found).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdateStatus))]
    public partial string UpdateStatus { get; set; } = "";

    /// <summary>True when there is an update status line to show, so the About tab stays quiet otherwise.</summary>
    public bool HasUpdateStatus => !string.IsNullOrEmpty(UpdateStatus);

    /// <summary>True when a newer release is available to install.</summary>
    [ObservableProperty]
    public partial bool UpdateAvailable { get; set; }

    /// <summary>True while checking or downloading an update, so its buttons disable.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotUpdating))]
    public partial bool UpdateInProgress { get; set; }

    /// <summary>Inverse of <see cref="UpdateInProgress"/>; the update buttons bind their enabled state to this.</summary>
    public bool IsNotUpdating => !UpdateInProgress;

    [ObservableProperty]
    public partial string GpuName { get; set; } = "-";

    [ObservableProperty]
    public partial string Firmware { get; set; } = "-";

    [ObservableProperty]
    public partial string PanelState { get; set; } = "-";

    [ObservableProperty]
    public partial string CurrentMode { get; set; } = "-";

    /// <summary>Whether the OS supports launching the app at login (Windows only for now).</summary>
    public bool StartupSupported => StartupService.IsSupported;

    [ObservableProperty]
    public partial bool StartWithWindows { get; set; }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (StartupService.IsEnabled() != value)
        {
            StartupService.SetEnabled(value);
            StatusMessage = value ? "AorusLcd will start with Windows." : "Autostart disabled.";
        }
    }

    /// <summary>Whether the system-tray icon is shown. Unchecking runs the app "service-only": closing the window exits the GUI.</summary>
    [ObservableProperty]
    public partial bool ShowTrayIcon { get; set; } = true;

    partial void OnShowTrayIconChanged(bool value)
    {
        if (_uiSettings.ShowTrayIcon == value)
        {
            return; // no-op on the constructor's initial sync; avoids a redundant disk write
        }
        _uiSettings.ShowTrayIcon = value;
        _uiSettings.Save();
        StatusMessage = value
            ? "Tray icon shown."
            : "Tray icon hidden - closing the window now exits the app (the background service keeps running).";
    }

    // ---- image -------------------------------------------------------------

    [ObservableProperty]
    public partial string? ImagePath { get; set; }

    /// <summary>The 320x170 preview of what will actually be shown on the panel (image/text/GIF), or null for firmware-drawn content.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview))]
    public partial Bitmap? PreviewImage { get; set; }

    /// <summary>True when a rendered bitmap preview is available (image/text/GIF); false shows the caption placeholder.</summary>
    public bool HasPreview => PreviewImage is not null;

    /// <summary>Caption under the preview, or the placeholder text for firmware-drawn content that can't be previewed.</summary>
    [ObservableProperty]
    public partial string? PreviewCaption { get; set; }

    /// <summary>The decoded source image, kept to re-render the exact 320x170 preview when the mode changes.</summary>
    private Bitmap? _imageSource;

    /// <summary>The GIF's first frame, kept for the preview.</summary>
    private Bitmap? _gifFirstFrame;

    /// <summary>Align the content selector to the panel's live mode only on the first successful connect, so a later manual refresh never clobbers an in-progress selection.</summary>
    private bool _hasSyncedContentToPanel;

    [ObservableProperty]
    public partial bool ClearSensorsOnSend { get; set; } = true;

    [ObservableProperty]
    public partial bool SaveOnSend { get; set; }

    // ---- sensor dashboard --------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AnySensorSelected))]
    [NotifyPropertyChangedFor(nameof(ShowServiceForSensors))]
    public partial bool ShowGpuTemp { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AnySensorSelected))]
    [NotifyPropertyChangedFor(nameof(ShowServiceForSensors))]
    public partial bool ShowGpuClock { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AnySensorSelected))]
    [NotifyPropertyChangedFor(nameof(ShowServiceForSensors))]
    public partial bool ShowGpuUsage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AnySensorSelected))]
    [NotifyPropertyChangedFor(nameof(ShowServiceForSensors))]
    public partial bool ShowFanSpeed { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AnySensorSelected))]
    [NotifyPropertyChangedFor(nameof(ShowServiceForSensors))]
    public partial bool ShowRamClock { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AnySensorSelected))]
    [NotifyPropertyChangedFor(nameof(ShowServiceForSensors))]
    public partial bool ShowRamUsage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AnySensorSelected))]
    [NotifyPropertyChangedFor(nameof(ShowServiceForSensors))]
    public partial bool ShowFps { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AnySensorSelected))]
    [NotifyPropertyChangedFor(nameof(ShowServiceForSensors))]
    public partial bool ShowTgp { get; set; }

    [ObservableProperty]
    public partial int SensorInterval { get; set; } = 4;

    /// <summary>True when at least one dashboard widget is selected (so the live feed - and its service - is needed).</summary>
    public bool AnySensorSelected => ShowGpuTemp || ShowGpuClock || ShowGpuUsage || ShowFanSpeed
        || ShowRamClock || ShowRamUsage || ShowFps || ShowTgp;

    // ---- background service -------------------------------------------------

    [ObservableProperty]
    public partial string ServiceStatusText { get; set; } = "-";

    [ObservableProperty]
    public partial bool ServiceInstalled { get; set; }

    [ObservableProperty]
    public partial bool ServiceRunning { get; set; }

    /// <summary>True on platforms where the background service is available (Windows).</summary>
    public bool ServiceSupported => OperatingSystem.IsWindows();

    /// <summary>Show the background-service controls only where they're needed: on Windows, once a live widget is selected.</summary>
    public bool ShowServiceForSensors => ServiceSupported && AnySensorSelected;

    // ---- LCD content selection --------------------------------------------

    /// <summary>Content types the panel can show; the panel displays exactly one at a time.</summary>
    public const string ContentImage = "Image";
    public const string ContentBuiltIn = "BuiltIn";
    public const string ContentText = "Text";
    public const string ContentGif = "Gif";
    public const string ContentCarousel = "Carousel";
    public const string ContentSensors = "Sensors";

    /// <summary>The currently selected panel content type (mutually exclusive - the panel shows one thing at a time).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsImageContent))]
    [NotifyPropertyChangedFor(nameof(IsBuiltInContent))]
    [NotifyPropertyChangedFor(nameof(IsTextContent))]
    [NotifyPropertyChangedFor(nameof(IsGifContent))]
    [NotifyPropertyChangedFor(nameof(IsCarouselContent))]
    [NotifyPropertyChangedFor(nameof(IsSensorContent))]
    public partial string SelectedLcdContent { get; set; } = ContentImage;

    public bool IsImageContent => SelectedLcdContent == ContentImage;
    public bool IsBuiltInContent => SelectedLcdContent == ContentBuiltIn;
    public bool IsTextContent => SelectedLcdContent == ContentText;
    public bool IsGifContent => SelectedLcdContent == ContentGif;
    public bool IsCarouselContent => SelectedLcdContent == ContentCarousel;
    public bool IsSensorContent => SelectedLcdContent == ContentSensors;

    partial void OnSelectedLcdContentChanged(string value) => UpdatePreview();

    /// <summary>Refresh the panel preview for the selected content: render image/text/GIF, or show a caption for firmware-drawn content.</summary>
    private void UpdatePreview()
    {
        Bitmap? preview = null;
        string? caption = null;
        switch (SelectedLcdContent)
        {
            case ContentImage:
                preview = _imageSource is not null ? PanelImage.Render320(_imageSource) : null;
                caption = preview is null
                    ? "The panel's current image can't be read back - choose an image to preview what you'll send."
                    : "Preview of the image you'll send - exactly as it appears on the 320×170 panel.";
                break;
            case ContentText:
                preview = string.IsNullOrEmpty(TextInput)
                    ? null
                    : PanelText.Render(TextInput, TextSize, SafeColor(TextColorHex, Colors.White), SafeColor(TextBgHex, Colors.Black));
                caption = preview is null
                    ? "Enter a message to preview what you'll send."
                    : "Preview of the text you'll send.";
                break;
            case ContentGif:
                preview = _gifFirstFrame is not null ? PanelImage.Render320(_gifFirstFrame) : null;
                caption = preview is null
                    ? "The panel's current GIF can't be read back - choose a GIF to preview what you'll send."
                    : "First frame of the GIF you'll send; the panel plays the full animation.";
                break;
            case ContentBuiltIn:
                caption = "Built-in animation drawn by the panel firmware - it can't be mirrored in this preview.";
                break;
            case ContentCarousel:
                caption = "Carousel rotates through the selected screens on the panel - it can't be mirrored in this preview.";
                break;
            case ContentSensors:
                caption = "Live GPU dashboard drawn by the panel from the sensor feed - it can't be mirrored in this preview.";
                break;
        }
        SetPreview(preview);
        PreviewCaption = caption;
    }

    /// <summary>Swap the preview bitmap, disposing the previous render-target so previews don't leak.</summary>
    private void SetPreview(Bitmap? next)
    {
        var old = PreviewImage;
        PreviewImage = next;
        if (!ReferenceEquals(old, next))
        {
            old?.Dispose();
        }
    }

    // ---- built-in modes / text / gif / carousel ----------------------------

    public ModePreset[] ModePresets { get; } =
    [
        new("Faith 1", LcdMode.Faith1),
        new("Faith 2", LcdMode.Faith2),
        new("Faith 3", LcdMode.Faith3),
        new("Chibi Clock", LcdMode.ChibTime),
    ];

    [ObservableProperty]
    public partial string TextInput { get; set; } = "AORUS";

    [ObservableProperty]
    public partial string TextColorHex { get; set; } = "FFFFFF";

    [ObservableProperty]
    public partial string TextBgHex { get; set; } = "000000";

    [ObservableProperty]
    public partial int TextSize { get; set; } = 40;

    partial void OnTextInputChanged(string value) => UpdateTextPreview();
    partial void OnTextColorHexChanged(string value) => UpdateTextPreview();
    partial void OnTextBgHexChanged(string value) => UpdateTextPreview();
    partial void OnTextSizeChanged(int value) => UpdateTextPreview();

    private void UpdateTextPreview()
    {
        if (IsTextContent)
        {
            UpdatePreview();
        }
    }

    /// <summary>Apply the panel's built-in rainbow effect to text (matches GCC's default).</summary>
    [ObservableProperty]
    public partial bool TextRainbowEffect { get; set; } = true;

    [ObservableProperty]
    public partial string? GifPath { get; set; }

    /// <summary>Set by the view to present a GIF file picker.</summary>
    public Func<Task<string?>>? GifPicker { get; set; }

    [ObservableProperty]
    public partial bool CarouselFaith1 { get; set; } = true;

    [ObservableProperty]
    public partial bool CarouselFaith2 { get; set; }

    [ObservableProperty]
    public partial bool CarouselFaith3 { get; set; }

    [ObservableProperty]
    public partial bool CarouselImage { get; set; } = true;

    [ObservableProperty]
    public partial bool CarouselChibi { get; set; }

    [ObservableProperty]
    public partial int CarouselInterval { get; set; } = 5;

    // ---- RGB ---------------------------------------------------------------

    public string[] RgbModes { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMultiColorMode))]
    public partial string SelectedRgbMode { get; set; }

    /// <summary>Editable ordered colour list; the first entry is the primary colour for single-colour effects.</summary>
    public ObservableCollection<RgbColorItem> RgbColorList { get; } = [new RgbColorItem("FF6600")];

    /// <summary>True for effects that blend multiple colours, so the extra colour editor shows.</summary>
    public bool IsMultiColorMode => SelectedRgbMode is "Color Shift" or "Tricolor";

    [ObservableProperty]
    public partial int RgbBrightness { get; set; } = 100;

    [ObservableProperty]
    public partial int RgbSpeed { get; set; } = 2;

    // ---- RGB (Blackwell / RTX 50-series) -----------------------------------

    /// <summary>True when the connected GPU is RTX 50-series (Blackwell RGB protocol); drives which RGB tab shows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLegacyRgb))]
    [NotifyPropertyChangedFor(nameof(ShowBlackwellRgb))]
    [NotifyPropertyChangedFor(nameof(IsAnyMultiColorMode))]
    [NotifyPropertyChangedFor(nameof(MaxColorCount))]
    public partial bool IsBlackwellRgb { get; set; }

    /// <summary>Show the legacy RGB tab until a Blackwell card is detected.</summary>
    public bool ShowLegacyRgb => !IsBlackwellRgb;

    /// <summary>Show the Blackwell RGB tab for RTX 50-series cards.</summary>
    public bool ShowBlackwellRgb => IsBlackwellRgb;

    public string[] RgbBlackwellModes { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBlackwellMultiColorMode))]
    public partial string SelectedRgbBlackwellMode { get; set; }

    /// <summary>True for the Blackwell effects that take multiple colours (Color Shift, Dazzle).</summary>
    public bool IsBlackwellMultiColorMode => SelectedRgbBlackwellMode is "Color Shift" or "Dazzle";

    /// <summary>Multi-colour state for whichever RGB tab is currently shown (legacy or Blackwell).</summary>
    public bool IsAnyMultiColorMode => IsBlackwellRgb ? IsBlackwellMultiColorMode : IsMultiColorMode;

    /// <summary>Most colours the active protocol accepts for the current effect (single-colour effects allow one).</summary>
    public int MaxColorCount => IsAnyMultiColorMode
        ? (IsBlackwellRgb ? RgbFusion2Blackwell.MaxColors : LegacyMaxColors)
        : 1;

    /// <summary>Legacy Color Shift carries four two-colour banks per zone.</summary>
    private const int LegacyMaxColors = 8;

    public bool CanAddColor => IsAnyMultiColorMode && RgbColorList.Count < MaxColorCount;

    public bool CanRemoveColor => RgbColorList.Count > 1;

    /// <summary>Banded preview of the colour list (crisp bands mirror the discrete effect colours).</summary>
    public IBrush RgbPreviewBrush
    {
        get
        {
            var colors = RgbColorList
                .Select(i => RgbColor.TryParse(i.Hex, out var c) ? Color.FromRgb(c.R, c.G, c.B) : (Color?)null)
                .Where(c => c.HasValue)
                .Select(c => c!.Value)
                .ToList();
            if (colors.Count == 0)
            {
                return new SolidColorBrush(Colors.Black);
            }
            if (colors.Count == 1)
            {
                return new SolidColorBrush(colors[0]);
            }
            var brush = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            };
            for (int i = 0; i < colors.Count; i++)
            {
                brush.GradientStops.Add(new GradientStop(colors[i], (double)i / colors.Count));
                brush.GradientStops.Add(new GradientStop(colors[i], (double)(i + 1) / colors.Count));
            }
            return brush;
        }
    }

    [RelayCommand(CanExecute = nameof(CanAddColor))]
    private void AddColor()
    {
        if (RgbColorList.Count < MaxColorCount)
        {
            RgbColorList.Add(new RgbColorItem("00AAFF"));
        }
    }

    [RelayCommand(CanExecute = nameof(CanRemoveColor))]
    private void RemoveColor(RgbColorItem? item)
    {
        if (item is not null && RgbColorList.Count > 1)
        {
            RgbColorList.Remove(item);
        }
    }

    partial void OnSelectedRgbModeChanged(string value) => RefreshColorEditor();

    partial void OnSelectedRgbBlackwellModeChanged(string value) => RefreshColorEditor();

    partial void OnIsBlackwellRgbChanged(bool value) => RefreshColorEditor();

    /// <summary>Re-evaluate the colour editor when the effect or protocol changes, trimming any colours the new mode can't carry.</summary>
    private void RefreshColorEditor()
    {
        OnPropertyChanged(nameof(IsAnyMultiColorMode));
        OnPropertyChanged(nameof(MaxColorCount));
        while (RgbColorList.Count > MaxColorCount && RgbColorList.Count > 1)
        {
            RgbColorList.RemoveAt(RgbColorList.Count - 1);
        }
        OnColorListChanged();
    }

    private void OnRgbColorListChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (RgbColorItem item in e.NewItems)
            {
                item.PropertyChanged += OnColorItemChanged;
            }
        }
        if (e.OldItems is not null)
        {
            foreach (RgbColorItem item in e.OldItems)
            {
                item.PropertyChanged -= OnColorItemChanged;
            }
        }
        OnColorListChanged();
    }

    private void OnColorItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RgbColorItem.Hex))
        {
            OnPropertyChanged(nameof(RgbPreviewBrush));
        }
    }

    private void OnColorListChanged()
    {
        OnPropertyChanged(nameof(RgbPreviewBrush));
        OnPropertyChanged(nameof(CanAddColor));
        OnPropertyChanged(nameof(CanRemoveColor));
        AddColorCommand.NotifyCanExecuteChanged();
        RemoveColorCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Restore the last-applied RGB config; the write-only controller can't be read, so we replay what the app last set.</summary>
    private void RestoreRgbSettings()
    {
        if (Array.IndexOf(RgbModes, _uiSettings.LastRgbMode) >= 0)
        {
            SelectedRgbMode = _uiSettings.LastRgbMode;
        }
        if (Array.IndexOf(RgbBlackwellModes, _uiSettings.LastRgbBlackwellMode) >= 0)
        {
            SelectedRgbBlackwellMode = _uiSettings.LastRgbBlackwellMode;
        }
        RgbBrightness = Math.Clamp(_uiSettings.LastRgbBrightness, 0, 100);
        RgbSpeed = Math.Clamp(_uiSettings.LastRgbSpeed, 0, 5);

        var saved = _uiSettings.LastRgbColors;
        if (saved is { Count: > 0 })
        {
            foreach (var item in RgbColorList)
            {
                item.PropertyChanged -= OnColorItemChanged;
            }
            RgbColorList.Clear();
            foreach (var hex in saved)
            {
                RgbColorList.Add(new RgbColorItem(hex));
            }
            OnColorListChanged();
        }
    }

    /// <summary>Save the current RGB config so the next launch reflects what the app last applied.</summary>
    private void PersistRgbSettings()
    {
        _uiSettings.LastRgbMode = SelectedRgbMode;
        _uiSettings.LastRgbBlackwellMode = SelectedRgbBlackwellMode;
        _uiSettings.LastRgbBrightness = RgbBrightness;
        _uiSettings.LastRgbSpeed = RgbSpeed;
        _uiSettings.LastRgbColors = [.. RgbColorList.Select(i => i.Hex)];
        _uiSettings.Save();
    }

    // ---- commands ----------------------------------------------------------

    [RelayCommand]
    private Task RefreshStatusAsync() => RunAsync("Reading panel status…", async () =>
    {
        var gpuName = await _hw.ConnectAsync();
        var status = await _hw.GetStatusAsync();
        var rgbKind = await DetectRgbKindAsync(gpuName);
        Dispatcher.UIThread.Post(() =>
        {
            GpuName = gpuName;
            IsBlackwellRgb = rgbKind == RgbControllerKind.Blackwell;
            Firmware = status.FirmwareVersion;
            PanelState = status.IsOn ? "On" : "Off";
            CurrentMode = $"{(int)status.Mode} ({status.Mode})";
            SetSensorToggles(status.DisplayElements);
            SensorInterval = status.DisplayInterval == 0 ? SensorInterval : status.DisplayInterval;
            if (!_hasSyncedContentToPanel)
            {
                // Start the preview on whatever the panel is actually showing instead of the Image default.
                SelectedLcdContent = MapPanelStateToContent(status.Mode, status.DisplayElements);
                _hasSyncedContentToPanel = true;
            }
        });
        return $"Connected: {gpuName} - firmware {status.FirmwareVersion}, mode {status.Mode}.";
    });

    /// <summary>Map the panel's live mode/overlay to a content selector value; an active sensor overlay wins since it's the panel's visible content.</summary>
    private static string MapPanelStateToContent(LcdMode mode, LcdDisplayElements elements)
    {
        if (elements != LcdDisplayElements.None)
        {
            return ContentSensors;
        }
        return mode switch
        {
            LcdMode.Image => ContentImage,
            LcdMode.Text => ContentText,
            LcdMode.Gif => ContentGif,
            LcdMode.Carousel => ContentCarousel,
            _ => ContentBuiltIn, // Faith1/2/3 + ChibTime are firmware screens
        };
    }

    /// <summary>Prefer the hardware-detected RGB generation; fall back to the GPU name when RGB can't be located.</summary>
    private async Task<RgbControllerKind> DetectRgbKindAsync(string gpuName)
    {
        try
        {
            var (_, _, kind) = await _hw.ConnectRgbAsync();
            return kind;
        }
        catch (Exception e) when (e is HardwareUnavailableException or NvApiException)
        {
            // RGB not reachable (no controller, or not on Windows): fall back to
            // the name-classified generation. Unexpected exceptions still surface.
            return _hw.RgbKind ?? RgbLocator.ClassifyByName(gpuName);
        }
    }

    [RelayCommand]
    private async Task BrowseImageAsync()
    {
        if (ImagePicker is null)
        {
            return;
        }
        var path = await ImagePicker();
        if (string.IsNullOrEmpty(path))
        {
            return;
        }
        ImagePath = path;
        try
        {
            // Decode off the UI thread, size-constrained, to avoid blocking or full-resolution allocations for the preview.
            var bitmap = await Task.Run(() =>
            {
                using var stream = System.IO.File.OpenRead(path);
                return Bitmap.DecodeToWidth(stream, 640, BitmapInterpolationMode.HighQuality);
            });
            _imageSource?.Dispose();
            _imageSource = bitmap;
            SelectedLcdContent = ContentImage;
            UpdatePreview();
            StatusMessage = $"Loaded {path}";
        }
        catch (Exception e)
        {
            StatusMessage = $"Could not load image: {e.Message}";
        }
    }

    [RelayCommand]
    private Task SendImageAsync()
    {
        if (string.IsNullOrEmpty(ImagePath))
        {
            StatusMessage = "Pick an image first.";
            return Task.CompletedTask;
        }
        var path = ImagePath;
        bool clear = ClearSensorsOnSend;
        bool save = SaveOnSend;
        return RunAsync("Uploading image…", async () =>
        {
            var le565 = await Task.Run(() => PanelImage.LoadLe565(path));
            await _hw.SendImageAsync(le565, clear, save);
            if (clear)
            {
                await DisableFeedConfigAsync(); // the dashboard was cleared; stop the background feed
            }
            return "Image sent" + (clear ? ", sensors off" : "") + (save ? ", saved" : "") + ".";
        });
    }

    [RelayCommand]
    private Task ApplySensorsAsync()
    {
        var elements = CollectSensorFlags();
        int interval = SensorInterval;
        return RunAsync("Applying sensor dashboard…", async () =>
        {
            if (elements == LcdDisplayElements.None)
            {
                return await DisableSensorsAsync();
            }

            // The service watches this config and drives E1/E3 so the feed continues after the GUI closes.
            await WriteFeedConfigAsync(elements, interval, enabled: true);
            string state = await EnsureServiceRunningForFeedAsync();
            return $"Sensors: {elements} - {state}";
        });
    }

    [RelayCommand]
    private Task SensorsOffAsync()
    {
        ClearAllSensorToggles();
        return RunAsync("Disabling sensor dashboard…", async () => await DisableSensorsAsync());
    }

    /// <summary>Stop feed config and clear the dashboard directly so widgets disappear immediately.</summary>
    private async Task<string> DisableSensorsAsync()
    {
        await WriteFeedConfigAsync(LcdDisplayElements.None, SensorInterval, enabled: false);
        await _hw.SetSensorsAsync(LcdDisplayElements.None, 0, SaveOnSend);
        return "Sensor dashboard off.";
    }

    [RelayCommand]
    private Task PanelOnAsync() => RunAsync("Turning panel on…", async () =>
    {
        await _hw.SetPanelPowerAsync(true);
        return "Panel on.";
    });

    [RelayCommand]
    private Task PanelOffAsync() => RunAsync("Turning panel off…", async () =>
    {
        await _hw.SetPanelPowerAsync(false);
        return "Panel off.";
    });

    [RelayCommand]
    private Task SaveToPanelAsync() => RunAsync("Saving to panel NVRAM…", async () =>
    {
        await _hw.SaveAsync();
        return "Saved to panel NVRAM (survives reboot).";
    });

    [RelayCommand]
    private Task SetModeAsync(ModePreset preset) => RunAsync($"Switching to {preset.Name}…", async () =>
    {
        await _hw.SetModeAsync(preset.Mode);
        if (SaveOnSend)
        {
            await _hw.SaveAsync();
        }
        return $"Mode: {preset.Name}.";
    });

    [RelayCommand]
    private Task SendTextAsync()
    {
        if (string.IsNullOrWhiteSpace(TextInput))
        {
            StatusMessage = "Enter some text first.";
            return Task.CompletedTask;
        }
        string text = TextInput;
        double size = TextSize;
        Color fg = SafeColor(TextColorHex, Colors.White);
        Color bg = SafeColor(TextBgHex, Colors.Black);
        bool clear = ClearSensorsOnSend;
        bool save = SaveOnSend;
        bool effect = TextRainbowEffect;
        return RunAsync("Rendering and sending text…", async () =>
        {
            var le565 = await Task.Run(() => PanelText.RenderLe565(text, size, fg, bg));
            await _hw.SendTextAsync(le565, clear, save, effect);
            if (clear)
            {
                await DisableFeedConfigAsync(); // the dashboard was cleared; stop the background feed
            }
            return $"Text \"{text}\" sent" + (effect ? ", rainbow" : "") + (save ? ", saved" : "") + ".";
        });
    }

    [RelayCommand]
    private async Task BrowseGifAsync()
    {
        if (GifPicker is null)
        {
            return;
        }
        var path = await GifPicker();
        if (!string.IsNullOrEmpty(path))
        {
            GifPath = path;
            try
            {
                var frame = await Task.Run(() =>
                {
                    using var stream = System.IO.File.OpenRead(path);
                    return Bitmap.DecodeToWidth(stream, 640, BitmapInterpolationMode.HighQuality);
                });
                _gifFirstFrame?.Dispose();
                _gifFirstFrame = frame;
                SelectedLcdContent = ContentGif;
                UpdatePreview();
            }
            catch (Exception)
            {
                // preview is best-effort; the GIF still sends even if the first frame won't decode for preview
            }
            StatusMessage = $"Selected GIF: {path}";
        }
    }

    [RelayCommand]
    private Task SendGifAsync()
    {
        if (string.IsNullOrEmpty(GifPath))
        {
            StatusMessage = "Pick a GIF first.";
            return Task.CompletedTask;
        }
        string path = GifPath;
        bool save = SaveOnSend;
        return RunAsync("Decoding and sending GIF…", async () =>
        {
            var (frames, delays) = await Task.Run(() => GifDecoder.DecodeLe565(path));
            await _hw.SendGifAsync(frames, delays, save);
            return $"GIF sent ({frames.Count} frames)" + (save ? ", saved" : "") + ".";
        });
    }

    [RelayCommand]
    private Task ApplyCarouselAsync()
    {
        var modes = new List<int>();
        if (CarouselFaith1) modes.Add((int)LcdMode.Faith1);
        if (CarouselFaith2) modes.Add((int)LcdMode.Faith2);
        if (CarouselFaith3) modes.Add((int)LcdMode.Faith3);
        if (CarouselImage) modes.Add((int)LcdMode.Image);
        if (CarouselChibi) modes.Add((int)LcdMode.ChibTime);
        if (modes.Count == 0)
        {
            StatusMessage = "Select at least one screen for the carousel.";
            return Task.CompletedTask;
        }
        int interval = CarouselInterval;
        bool save = SaveOnSend;
        return RunAsync("Applying carousel…", async () =>
        {
            await _hw.SetCarouselAsync(modes, interval, save);
            return $"Carousel: [{string.Join(",", modes)}] every {interval}s.";
        });
    }

    [RelayCommand]
    private Task ApplyRgbAsync()
    {
        var color = PrimaryColor();
        var mode = SelectedRgbMode;
        byte brightness = (byte)(Math.Clamp(RgbBrightness, 0, 100) * RgbFusion2.BrightnessMax / 100);
        byte speed = (byte)Math.Clamp(RgbSpeed, RgbFusion2.SpeedSlowest, RgbFusion2.SpeedFastest);
        // The color-bank effects blend several colours; single-colour effects
        // just use the first.
        RgbColor[] colors = IsMultiColorMode ? CollectRgbColors() : [color];
        return RunAsync("Applying RGB…", async () =>
        {
            if (mode == "Static")
            {
                await _hw.SetRgbStaticAsync(color, brightness);
            }
            else
            {
                await _hw.SetRgbEffectAsync(MapRgbMode(mode), colors, speed, brightness);
            }
            PersistRgbSettings();
            string colorText = IsMultiColorMode ? $"{colors.Length} colors" : $"#{PrimaryHex}";
            return $"RGB: {mode} {colorText} brightness {RgbBrightness}%.";
        });
    }

    /// <summary>First list colour parsed for single-colour effects; falls back to black.</summary>
    private RgbColor PrimaryColor()
        => RgbColor.TryParse(PrimaryHex, out var c) ? c : RgbColor.Black;

    private string PrimaryHex => RgbColorList.Count > 0 ? RgbColorList[0].Hex : "000000";

    /// <summary>Ordered list of the valid colours for the multi-colour effects.</summary>
    private RgbColor[] CollectRgbColors()
    {
        var colors = new List<RgbColor>(RgbColorList.Count);
        foreach (var item in RgbColorList)
        {
            if (RgbColor.TryParse(item.Hex, out var c))
            {
                colors.Add(c);
            }
        }
        return colors.Count > 0 ? [.. colors] : [RgbColor.Black];
    }

    [RelayCommand]
    private Task RgbOffAsync() => RunAsync("Turning RGB off…", async () =>
    {
        await _hw.RgbOffAsync();
        return "RGB off.";
    });

    [RelayCommand]
    private Task ApplyRgbBlackwellAsync()
    {
        var color = PrimaryColor();
        var modeName = SelectedRgbBlackwellMode;
        var mode = MapBlackwellMode(modeName);
        byte brightness = (byte)Math.Clamp(
            (int)Math.Round(Math.Clamp(RgbBrightness, 0, 100) / 100.0 * RgbFusion2Blackwell.BrightnessMax),
            RgbFusion2Blackwell.BrightnessMin, RgbFusion2Blackwell.BrightnessMax);
        byte speed = (byte)Math.Clamp(RgbSpeed + 1,
            RgbFusion2Blackwell.SpeedSlowest, RgbFusion2Blackwell.SpeedFastest);
        RgbColor[] colors = IsBlackwellMultiColorMode ? CollectRgbColors() : [color];
        return RunAsync("Applying RGB…", async () =>
        {
            if (mode == RgbBlackwellMode.Static)
            {
                await _hw.SetRgbBlackwellStaticAsync(color, brightness);
            }
            else
            {
                await _hw.SetRgbBlackwellEffectAsync(mode, colors, speed, brightness);
            }
            PersistRgbSettings();
            string colorText = IsBlackwellMultiColorMode ? $"{colors.Length} colors" : $"#{PrimaryHex}";
            return $"RGB: {modeName} {colorText} brightness {RgbBrightness}%.";
        });
    }

    [RelayCommand]
    private Task RgbBlackwellOffAsync() => RunAsync("Turning RGB off…", async () =>
    {
        await _hw.RgbBlackwellOffAsync();
        return "RGB off.";
    });

    // ---- background service commands ---------------------------------------

    [RelayCommand]
    private Task InstallServiceAsync() => RunAsync("Installing background service…", async () =>
    {
        await _service.InstallAsync();
        await RefreshServiceStateAfterDelayAsync();
        return "Background service installed and started.";
    });

    [RelayCommand]
    private Task UninstallServiceAsync() => RunAsync("Removing background service…", async () =>
    {
        await _service.UninstallAsync();
        await RefreshServiceStateAfterDelayAsync();
        return "Background service removed.";
    });

    [RelayCommand]
    private Task StartServiceAsync() => RunAsync("Starting background service…", async () =>
    {
        await _service.StartAsync();
        await RefreshServiceStateAfterDelayAsync();
        return "Background service started.";
    });

    [RelayCommand]
    private Task StopServiceAsync() => RunAsync("Stopping background service…", async () =>
    {
        await _service.StopAsync();
        await RefreshServiceStateAfterDelayAsync();
        return "Background service stopped.";
    });

    // ---- auto-update commands ----------------------------------------------

    [RelayCommand]
    private Task CheckForUpdatesAsync() => CheckForUpdatesCoreAsync(silent: false);

    /// <summary>Check GitHub for a newer release; in silent mode stay quiet unless one is found, otherwise always report the outcome.</summary>
    private async Task CheckForUpdatesCoreAsync(bool silent)
    {
        if (UpdateInProgress)
        {
            return;
        }
        if (!UpdatesSupported)
        {
            if (!silent)
            {
                UpdateStatus = "Automatic updates are available on the Windows build only.";
            }
            return;
        }
        UpdateInProgress = true;
        if (!silent)
        {
            UpdateStatus = "Checking for updates…";
        }
        try
        {
            var update = await _update.CheckForUpdateAsync().ConfigureAwait(true);
            if (update is not null)
            {
                _pendingUpdate = update;
                UpdateAvailable = true;
                UpdateStatus = $"Update available: {update.TagName} (you have {_update.CurrentVersion.ToString(3)}).";
            }
            else
            {
                // Clear any stale result from an earlier check so the "Update now" button can't linger.
                _pendingUpdate = null;
                UpdateAvailable = false;
                if (!silent)
                {
                    UpdateStatus = $"You're up to date ({_update.CurrentVersion.ToString(3)}).";
                }
            }
        }
        catch (Exception e)
        {
            if (!silent)
            {
                UpdateStatus = $"Couldn't check for updates: {e.Message}";
            }
        }
        finally
        {
            UpdateInProgress = false;
        }
    }

    [RelayCommand]
    private async Task DownloadAndInstallUpdateAsync()
    {
        if (_pendingUpdate is not { } update || UpdateInProgress || !OperatingSystem.IsWindows())
        {
            return;
        }
        if (IsBusy)
        {
            // A hardware transfer is in flight; shutdown waits for it, which would stall the installer.
            UpdateStatus = "Finish the current panel operation before installing the update.";
            return;
        }
        UpdateInProgress = true;
        try
        {
            var progress = new Progress<double>(p => UpdateStatus = $"Downloading {update.TagName}… {p * 100:0}%");
            UpdateStatus = $"Downloading {update.TagName}…";
            var setupPath = await _update.DownloadSetupAsync(update, progress).ConfigureAwait(true);

            UpdateStatus = "Verifying installer…";
            var signature = await Task.Run(() => InstallerVerifier.Verify(setupPath)).ConfigureAwait(true);
            if (signature == InstallerSignature.Invalid)
            {
                TryDelete(setupPath);
                UpdateStatus = "Update blocked: the installer failed signature verification (it may be tampered or untrusted). Download it manually from the releases page.";
                UpdateInProgress = false;
                return;
            }

            UpdateStatus = signature == InstallerSignature.Trusted
                ? "Signature verified. Launching installer…"
                : "Launching installer (note: this download isn't code-signed)…";
            UpdateService.LaunchInstaller(setupPath);
            // The installer needs to replace this exe, so exit once it has launched.
            ExitRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception e)
        {
            UpdateStatus = $"Update failed: {e.Message}";
            UpdateInProgress = false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            System.IO.File.Delete(path);
        }
        catch (Exception)
        {
            // best-effort cleanup of a rejected download
        }
    }

    // ---- helpers -----------------------------------------------------------

    private Task _currentOperation = Task.CompletedTask;

    private Task RunAsync(string busyMessage, Func<Task<string>> action)
    {
        if (IsBusy)
        {
            return Task.CompletedTask;
        }
        _currentOperation = RunCoreAsync(busyMessage, action);
        return _currentOperation;
    }

    private async Task RunCoreAsync(string busyMessage, Func<Task<string>> action)
    {
        IsBusy = true;
        StatusMessage = busyMessage;
        try
        {
            StatusMessage = await action();
        }
        catch (Exception e)
        {
            StatusMessage = $"Error: {e.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private LcdDisplayElements CollectSensorFlags()
    {
        var e = LcdDisplayElements.None;
        if (ShowGpuTemp) e |= LcdDisplayElements.GpuTemp;
        if (ShowGpuClock) e |= LcdDisplayElements.GpuClock;
        if (ShowGpuUsage) e |= LcdDisplayElements.GpuUsage;
        if (ShowFanSpeed) e |= LcdDisplayElements.FanSpeed;
        if (ShowRamClock) e |= LcdDisplayElements.RamClock;
        if (ShowRamUsage) e |= LcdDisplayElements.RamUsage;
        if (ShowFps) e |= LcdDisplayElements.Fps;
        if (ShowTgp) e |= LcdDisplayElements.Tgp;
        return e;
    }

    private void SetSensorToggles(LcdDisplayElements e)
    {
        ShowGpuTemp = e.HasFlag(LcdDisplayElements.GpuTemp);
        ShowGpuClock = e.HasFlag(LcdDisplayElements.GpuClock);
        ShowGpuUsage = e.HasFlag(LcdDisplayElements.GpuUsage);
        ShowFanSpeed = e.HasFlag(LcdDisplayElements.FanSpeed);
        ShowRamClock = e.HasFlag(LcdDisplayElements.RamClock);
        ShowRamUsage = e.HasFlag(LcdDisplayElements.RamUsage);
        ShowFps = e.HasFlag(LcdDisplayElements.Fps);
        ShowTgp = e.HasFlag(LcdDisplayElements.Tgp);
    }

    private void ClearAllSensorToggles() => SetSensorToggles(LcdDisplayElements.None);

    /// <summary>Persist the dashboard config the background service consumes.</summary>
    private Task WriteFeedConfigAsync(LcdDisplayElements elements, int intervalSeconds, bool enabled)
        => new FeedConfig
        {
            DisplayElements = (uint)elements,
            IntervalSeconds = intervalSeconds,
            Enabled = enabled,
        }.SaveAsync();

    /// <summary>Flip the persisted feed config to disabled without touching the toggles.</summary>
    private async Task DisableFeedConfigAsync()
    {
        var current = await FeedConfig.LoadAsync().ConfigureAwait(false);
        await WriteFeedConfigAsync(current.Elements, current.IntervalSeconds, enabled: false).ConfigureAwait(false);
    }

    /// <summary>Ensure the service can act on the config: start it if stopped, prompt install if missing, and return status text.</summary>
    private async Task<string> EnsureServiceRunningForFeedAsync()
    {
        var state = await Task.Run(_service.GetState);
        switch (state)
        {
            case ServiceState.Running:
                RefreshServiceState();
                return "live feed running via background service.";
            case ServiceState.Stopped:
                await _service.StartAsync();
                await RefreshServiceStateAfterDelayAsync();
                return "background service started; live feed running.";
            case ServiceState.NotInstalled:
                RefreshServiceState();
                return "saved - install the background service (Device tab) to drive the panel.";
            default:
                RefreshServiceState();
                return "saved; background service is changing state.";
        }
    }

    private void RefreshServiceState()
    {
        // GetState opens a ServiceController and queries the SCM - keep it off
        // the UI thread, then marshal the result back to update bound properties.
        _ = Task.Run(() =>
        {
            var state = _service.GetState();
            Dispatcher.UIThread.Post(() => ApplyServiceState(state));
        });
    }

    private void ApplyServiceState(ServiceState state)
    {
        ServiceInstalled = state is ServiceState.Stopped or ServiceState.Running or ServiceState.Transitioning;
        ServiceRunning = state == ServiceState.Running;
        ServiceStatusText = state switch
        {
            ServiceState.Running => "Installed - running",
            ServiceState.Stopped => "Installed - stopped",
            ServiceState.Transitioning => "Installed - changing state…",
            ServiceState.NotInstalled => "Not installed",
            _ => "Not available on this platform",
        };
    }

    private async Task RefreshServiceStateAfterDelayAsync()
    {
        await Task.Delay(800).ConfigureAwait(false); // let the SCM settle after a state change
        RefreshServiceState();
    }

    /// <summary>Await in-flight hardware transfers before shutdown so uploads finish with END; leave service running afterward.</summary>
    public async Task ShutdownAsync()
    {
        try
        {
            // Bounded so a stuck operation can't block exit forever (the bus lock
            // itself times out at 60s); normal uploads finish in a few seconds.
            await _currentOperation.WaitAsync(TimeSpan.FromSeconds(75));
        }
        catch
        {
            // Timed out or the operation faulted - proceed with shutdown anyway.
        }
        Dispatcher.UIThread.Post(() =>
        {
            PreviewImage?.Dispose();
            _imageSource?.Dispose();
            _gifFirstFrame?.Dispose();
        });
    }

    private static RgbMode MapRgbMode(string name) => name switch
    {
        "Breathing" => RgbMode.Breathing,
        "Color Cycle" => RgbMode.ColorCycle,
        "Flash" => RgbMode.Flashing,
        "Wave" => RgbMode.Wave,
        "Gradient" => RgbMode.Gradient,
        "Color Shift" => RgbMode.ColorShift,
        "Dual Flash" => RgbMode.DualFlashing,
        "Tricolor" => RgbMode.Tricolor,
        _ => RgbMode.Static,
    };

    private static RgbBlackwellMode MapBlackwellMode(string name) => name switch
    {
        "Direct" => RgbBlackwellMode.Direct,
        "Pulse" => RgbBlackwellMode.Breathing,
        "Flash" => RgbBlackwellMode.Flashing,
        "Double Flash" => RgbBlackwellMode.DualFlashing,
        "Color Cycle" => RgbBlackwellMode.ColorCycle,
        "Wave" => RgbBlackwellMode.Wave,
        "Gradient" => RgbBlackwellMode.Gradient,
        "Color Shift" => RgbBlackwellMode.ColorShift,
        "Dazzle" => RgbBlackwellMode.Dazzle,
        _ => RgbBlackwellMode.Static,
    };

    private static Color SafeColor(string hex, Color fallback)
        => RgbColor.TryParse(hex, out var c) ? Color.FromRgb(c.R, c.G, c.B) : fallback;
}
