namespace Puck.Scripting;

/// <summary>Specifies the digital-button bits packed into the snapshot's <c>buttons</c> field (A.4). This
/// numbering is the button-input view and is intentionally independent of the <c>padId</c> output numbering
/// in <see cref="PadCommandId"/>.</summary>
[Flags]
public enum AddonButtons : uint {
    /// <summary>No buttons held.</summary>
    None = 0u,

    /// <summary>The South face button (bit 0).</summary>
    South = (1u << 0),

    /// <summary>The East face button (bit 1).</summary>
    East = (1u << 1),

    /// <summary>The West face button (bit 2).</summary>
    West = (1u << 2),

    /// <summary>The North face button (bit 3).</summary>
    North = (1u << 3),

    /// <summary>The left shoulder button (bit 4).</summary>
    ShoulderL = (1u << 4),

    /// <summary>The right shoulder button (bit 5).</summary>
    ShoulderR = (1u << 5),

    /// <summary>The left trigger, as a digital press (bit 6).</summary>
    TriggerL = (1u << 6),

    /// <summary>The right trigger, as a digital press (bit 7).</summary>
    TriggerR = (1u << 7),

    /// <summary>The D-pad up direction (bit 8).</summary>
    DpadUp = (1u << 8),

    /// <summary>The D-pad down direction (bit 9).</summary>
    DpadDown = (1u << 9),

    /// <summary>The D-pad left direction (bit 10).</summary>
    DpadLeft = (1u << 10),

    /// <summary>The D-pad right direction (bit 11).</summary>
    DpadRight = (1u << 11),

    /// <summary>The Start button (bit 12).</summary>
    Start = (1u << 12),

    /// <summary>The Select button (bit 13).</summary>
    Select = (1u << 13),

    /// <summary>The left stick click (bit 14).</summary>
    StickL = (1u << 14),

    /// <summary>The right stick click (bit 15).</summary>
    StickR = (1u << 15),
}
