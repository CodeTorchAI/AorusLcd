namespace AorusLcd.Core;

/// <summary>What a recovery run does to the <c>E1</c> sensor overlay.</summary>
public enum OverlayAction
{
    /// <summary>Send no <c>E1</c> at all, leaving whatever the panel already has.</summary>
    Leave,

    /// <summary>Enable the GPU-temp + TGP dashboard widgets.</summary>
    Enable,

    /// <summary>Send <c>E1</c> with every widget disabled.</summary>
    Clear,
}
