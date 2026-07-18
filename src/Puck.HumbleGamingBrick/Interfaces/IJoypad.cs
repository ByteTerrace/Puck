namespace Puck.HumbleGamingBrick.Interfaces;

/// <summary>
/// The joypad register (P1/JOYP at <c>0xFF00</c>) and its button matrix. The CPU selects one of the two button groups
/// through the register and reads the selected lines back active-low; the component raises the joypad interrupt when a
/// selected line falls. Input arrives through <see cref="SetButtons"/> as plain state the host applies at a
/// deterministic point, so a run stays reproducible and snapshots round-trip.
/// </summary>
public interface IJoypad {
    /// <summary>Gets whether any button is currently held, regardless of the register's group selection — the raw key
    /// state that wakes the machine from stop mode.</summary>
    bool AnyButtonHeld { get; }

    /// <summary>Reads the joypad register: the two selection bits as last written, the four selected input lines
    /// active-low (0 = held), and the two unused high bits as 1.</summary>
    /// <returns>The register value the CPU observes.</returns>
    byte ReadRegister();
    /// <summary>Writes the joypad register; only the two group-selection bits (P14/P15) are writable, and changing them
    /// can drop a line low and raise the interrupt.</summary>
    /// <param name="value">The value being written.</param>
    void WriteRegister(byte value);
    /// <summary>Sets the full held-button state. The host calls this at a deterministic point (the run/frame seam) so
    /// input is part of the reproducible timeline; a newly held-and-selected button raises the joypad interrupt.</summary>
    /// <param name="pressed">The buttons currently held.</param>
    void SetButtons(JoypadButtons pressed);
}
