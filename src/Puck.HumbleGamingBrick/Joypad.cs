namespace Puck.HumbleGamingBrick;

/// <summary>
/// The joypad register (<c>P1</c>/<c>JOYP</c>, <c>0xFF00</c>). The CPU writes bits 5-4 to select a button group —
/// <c>P15</c> (bit 5) for the action buttons, <c>P14</c> (bit 4) for the direction pad, both active-low — and reads
/// the selected group's state in bits 3-0, where a pressed button reads as 0. Pressing a button drives its input
/// line low, which requests the joypad interrupt (the mechanism that also wakes the CPU from <c>STOP</c>).
/// </summary>
public sealed class Joypad : IJoypad {
    private const byte SelectDirections = 0x10;
    private const byte SelectActions = 0x20;

    private readonly IInterruptController m_interrupts;

    private byte m_pressed;
    private byte m_select;

    /// <summary>Initializes the joypad wired to the interrupt controller it raises the joypad interrupt through.</summary>
    /// <param name="interrupts">The interrupt controller.</param>
    /// <exception cref="ArgumentNullException"><paramref name="interrupts"/> is <see langword="null"/>.</exception>
    public Joypad(IInterruptController interrupts) {
        ArgumentNullException.ThrowIfNull(interrupts);

        m_interrupts = interrupts;
    }

    /// <summary>Reads the joypad register: the unused upper bits as one, the selected group, and the selected
    /// group's button states (0 = pressed) in the low nibble.</summary>
    /// <returns>The register value.</returns>
    public byte Read() {
        var inputs = 0x0F;

        // Both groups can be selected at once; their pressed buttons combine (active-low, so AND the masks).
        if ((m_select & SelectDirections) == 0) {
            inputs &= ~(m_pressed & 0x0F);
        }

        if ((m_select & SelectActions) == 0) {
            inputs &= ~((m_pressed >> 4) & 0x0F);
        }

        return (byte)(0xC0 | m_select | (inputs & 0x0F));
    }
    /// <summary>Writes the joypad register; only the two group-select bits (5-4) are writable.</summary>
    /// <param name="value">The value written.</param>
    public void Write(byte value) =>
        m_select = (byte)(value & 0x30);

    /// <summary>Sets the pressed state of a button, requesting the joypad interrupt on a fresh press.</summary>
    /// <param name="button">The button to update.</param>
    /// <param name="pressed">Whether the button is now held.</param>
    public void SetButton(JoypadButton button, bool pressed) {
        var mask = (byte)button;
        var wasPressed = ((m_pressed & mask) != 0);

        m_pressed = (pressed
            ? (byte)(m_pressed | mask)
            : (byte)(m_pressed & ~mask));

        // A button going from released to held drives an input line low and requests the joypad interrupt.
        if (pressed && !wasPressed) {
            m_interrupts.Request(kind: InterruptKind.Joypad);
        }
    }
}
