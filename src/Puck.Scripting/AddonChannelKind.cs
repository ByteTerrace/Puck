namespace Puck.Scripting;

/// <summary>The addon ABI's channel kind wire values (byte 0 of a 16-byte channel descriptor). Pinned
/// independently of any consumer enum. Ordinals 4 and 5 (formerly <c>Geometry</c>/<c>Overlay</c>, a Presentation-lane
/// pair that never shipped a consuming host) RETIRE PERMANENTLY as of the lane-axis deletion (owner ruling,
/// 2026-08-02) — never reused. A descriptor naming either byte refuses at decode as an undefined kind, through the
/// ordinary <c>Enum.IsDefined</c> check every unrecognized kind already goes through; there is no special-casing to
/// maintain for the retired values.</summary>
public enum AddonChannelKind : byte {
    /// <summary>The guest's declared input-source table: <c>Act</c> cells carry the addon's own virtual input device.</summary>
    Input = 1,

    /// <summary>The closed numeric query vocabulary a guest speaks <c>Act</c> and <c>Ask</c> cells through.</summary>
    Request = 2,

    /// <summary>The host-written answer channel paired with <see cref="Request"/>; the guest never writes it.</summary>
    Response = 3,
}
