namespace Puck.GameBoy;

/// <summary>
/// The joypad register (0xFF00): two selectable 4-button nibbles (direction pad and action buttons), with pressed
/// inputs reading as zero and a high-to-low edge requesting the joypad interrupt.
/// </summary>
public interface IJoypad {
    /// <summary>Reads the joypad register for the currently selected button group.</summary>
    byte Read();
    /// <summary>Writes the joypad register, selecting which button group is reported.</summary>
    void Write(byte value);
    /// <summary>Sets the pressed state of a button.</summary>
    void SetButton(JoypadButton button, bool pressed);
}
