namespace Puck.Abstractions.Machines;

/// <summary>
/// The digital buttons of a standard controller image, as a bit set — the neutral face/shoulder/system/direction
/// vocabulary a <see cref="MachinePadState"/> carries, independent of any host machine. Face buttons use the
/// platform-neutral South/East/West/North naming (South is the bottom face button), so a machine maps the subset it
/// understands (an SM83 brick reads South→A / East→B / Start / Back→Select; an N64-class machine reads more). A machine
/// never sees this type directly — its engine's adapter folds a <see cref="MachinePadState"/> down to the machine's own
/// button image.
/// </summary>
[Flags]
public enum MachineButtons : uint {
    /// <summary>No button held.</summary>
    None = 0u,
    /// <summary>The bottom face button (Xbox A / PlayStation Cross / Switch B).</summary>
    South = (1u << 0),
    /// <summary>The right face button (Xbox B / PlayStation Circle / Switch A).</summary>
    East = (1u << 1),
    /// <summary>The left face button (Xbox X / PlayStation Square / Switch Y).</summary>
    West = (1u << 2),
    /// <summary>The top face button (Xbox Y / PlayStation Triangle / Switch X).</summary>
    North = (1u << 3),
    /// <summary>The up direction of the directional pad.</summary>
    DpadUp = (1u << 4),
    /// <summary>The down direction of the directional pad.</summary>
    DpadDown = (1u << 5),
    /// <summary>The left direction of the directional pad.</summary>
    DpadLeft = (1u << 6),
    /// <summary>The right direction of the directional pad.</summary>
    DpadRight = (1u << 7),
    /// <summary>The left shoulder (bumper) button.</summary>
    LeftShoulder = (1u << 8),
    /// <summary>The right shoulder (bumper) button.</summary>
    RightShoulder = (1u << 9),
    /// <summary>The start / menu / plus button.</summary>
    Start = (1u << 10),
    /// <summary>The back / select / view / minus button.</summary>
    Back = (1u << 11),
}
