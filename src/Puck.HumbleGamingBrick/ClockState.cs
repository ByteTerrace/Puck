namespace Puck.HumbleGamingBrick;

/// <summary>
/// The CGB double-speed (KEY1) clock state, shared between the bus — which toggles it on a STOP speed switch and
/// derives the per-machine-cycle dot count from it — and the APU, whose generators advance at the switched rate.
/// A single per-machine instance keeps both reading one source of truth without a bus&#8596;APU dependency cycle.
/// </summary>
public sealed class ClockState {
    /// <summary>Gets or sets whether the CPU is running at the CGB's doubled clock.</summary>
    public bool DoubleSpeed { get; set; }
}
