namespace Puck.HumbleGamingBrick;

/// <summary>
/// How 15-bit CGB palette colors are mapped to RGBA for display. The CGB's LCD
/// does not show its raw digital colors directly; a correction curve reproduces the muted, slightly green-tinted look
/// of the physical screen.
/// </summary>
public enum CgbColorCorrection {
    /// <summary>No correction: each 5-bit channel is bit-expanded to 8 bits, giving the bright, saturated raw colors.</summary>
    Disabled,

    /// <summary>The measured per-channel response curve only, with no cross-channel mixing.</summary>
    CorrectCurves,

    /// <summary>The response curve plus a gamma-correct green/blue blend — the closest match to the
    /// physical CGB screen.</summary>
    ModernBalanced,
}
