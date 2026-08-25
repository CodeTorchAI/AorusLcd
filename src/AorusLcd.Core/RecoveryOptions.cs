using AorusLcd.Core.Rgb;

namespace AorusLcd.Core;

/// <summary>Knobs for <see cref="PanelRecovery"/>. Defaults reproduce a full recovery into Faith 1 as described in docs/RECOVERY.md.</summary>
public sealed record RecoveryOptions
{
    /// <summary>Mode to leave the panel in once the framebuffer is repainted.</summary>
    public LcdMode TargetMode { get; init; } = LcdMode.Faith1;

    /// <summary>Fill color of the frame uploaded to clear the wedge.</summary>
    public RgbColor FillColor { get; init; } = RgbColor.Black;

    /// <summary>Send the <c>E7</c> off/on power cycle before repainting.</summary>
    public bool PowerCycle { get; init; } = true;

    /// <summary>Upload a fresh frame. Without it an <c>E5</c> SetMode alone will not repaint a wedged panel.</summary>
    public bool Upload { get; init; } = true;

    public OverlayAction Overlay { get; init; } = OverlayAction.Enable;

    /// <summary>Persist to panel NVRAM. Leave off when GIGABYTE Control Center owns the configuration.</summary>
    public bool Save { get; init; } = true;
}
