using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.HumbleGamingBrick;

/// <summary>
/// The joypad hardware. It is not clocked: the register read is a combinational function of the selection bits and the
/// held buttons, and the joypad interrupt is produced by discrete events — a CPU write to the register or a host input
/// change — so it carries no free-running counter, only snapshottable state. A selected input line falling from high to
/// low (a button becoming readable as held) raises <see cref="InterruptKind.Joypad"/>.
/// </summary>
public sealed class JoypadComponent : IJoypad, ISnapshotable {
    private const byte DirectionSelect = 0x10;
    private const byte SelectMask = 0x30;
    private const byte ActionSelect = 0x20;

    private readonly IInterruptController m_interrupts;

    private byte m_buttons;
    private byte m_previousLine;
    private byte m_select;

    /// <summary>Creates the joypad wired to the interrupt controller it raises the joypad line on, with no buttons held
    /// and neither group selected (the register reads <c>0xFF</c>).</summary>
    /// <param name="interrupts">The interrupt controller.</param>
    /// <exception cref="ArgumentNullException"><paramref name="interrupts"/> is <see langword="null"/>.</exception>
    public JoypadComponent(IInterruptController interrupts) {
        ArgumentNullException.ThrowIfNull(argument: interrupts);

        m_interrupts = interrupts;
        m_select = SelectMask;
        m_previousLine = 0x0F;
    }

    /// <inheritdoc/>
    public bool AnyButtonHeld =>
        (m_buttons != 0);

    /// <inheritdoc/>
    public byte ReadRegister() =>
        (byte)(0xC0 | m_select | CurrentLine());
    /// <inheritdoc/>
    public void WriteRegister(byte value) {
        m_select = (byte)(value & SelectMask);

        UpdateInterrupt();
    }
    /// <inheritdoc/>
    public void SetButtons(JoypadButtons pressed) {
        m_buttons = (byte)pressed;

        UpdateInterrupt();
    }
    /// <inheritdoc/>
    public void SaveState(StateWriter writer) {
        writer.WriteByte(value: m_buttons);
        writer.WriteByte(value: m_select);
        writer.WriteByte(value: m_previousLine);
    }
    /// <inheritdoc/>
    public void LoadState(StateReader reader) {
        m_buttons = reader.ReadByte();
        m_select = reader.ReadByte();
        m_previousLine = reader.ReadByte();
    }

    // The four input lines (P10–P13) active-low for the currently selected group(s): a bit is 0 when its group is
    // selected and the button is held. A group is selected when its select bit is driven low.
    private byte CurrentLine() {
        var held = 0;

        if ((m_select & DirectionSelect) == 0) {
            held |= (m_buttons & 0x0F);
        }

        if ((m_select & ActionSelect) == 0) {
            held |= ((m_buttons >> 4) & 0x0F);
        }

        return (byte)(~held & 0x0F);
    }
    // The joypad interrupt fires on the high→low edge of any input line — a line that was released and is now held.
    private void UpdateInterrupt() {
        var line = CurrentLine();

        if ((m_previousLine & ~line & 0x0F) != 0) {
            m_interrupts.Request(kind: InterruptKind.Joypad);
        }

        m_previousLine = line;
    }
}
