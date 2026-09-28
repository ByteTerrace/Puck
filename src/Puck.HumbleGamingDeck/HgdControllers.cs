namespace Puck.HumbleGamingDeck;

/// <summary>The buttons of a standard controller, in the order its shift register reports them.</summary>
[Flags]
public enum HgdButtons : byte {
    /// <summary>No button.</summary>
    None = 0,
    /// <summary>The A button, the first bit read.</summary>
    A = (1 << 0),
    /// <summary>The B button.</summary>
    B = (1 << 1),
    /// <summary>The Select button.</summary>
    Select = (1 << 2),
    /// <summary>The Start button.</summary>
    Start = (1 << 3),
    /// <summary>Up on the control pad.</summary>
    Up = (1 << 4),
    /// <summary>Down on the control pad.</summary>
    Down = (1 << 5),
    /// <summary>Left on the control pad.</summary>
    Left = (1 << 6),
    /// <summary>Right on the control pad, the eighth bit read.</summary>
    Right = (1 << 7),
}
/// <summary>
/// The two controller ports with a standard controller in each. Writing $4016 bit 0 drives the strobe: while it is high
/// each controller keeps reloading its buttons, and once it falls every read of $4016 or $4017 returns the next button
/// of that port on data bit 0 and shifts, reporting 1 after all eight. Bits 5-7 of a read are CPU open bus.
/// https://www.nesdev.org/wiki/Standard_controller
/// </summary>
public sealed class HgdControllers : ISnapshotable {
    private readonly byte[] m_buttons = new byte[2];
    private readonly byte[] m_shift = new byte[2];

    private bool m_strobe;

    /// <summary>Sets the buttons held on a port, which the controller loads while its strobe is high.</summary>
    /// <param name="port">The port, 0 for $4016 or 1 for $4017.</param>
    /// <param name="buttons">The held buttons.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="port"/> is not 0 or 1.</exception>
    public void SetButtons(int port, HgdButtons buttons) {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            other: 1U,
            value: ((uint)port)
        );

        m_buttons[port] = ((byte)buttons);
        if (m_strobe) {
            m_shift[port] = m_buttons[port];
        }
    }
    /// <summary>Writes $4016: bit 0 is the strobe.</summary>
    /// <param name="value">The data byte.</param>
    public void Write(byte value) {
        m_strobe = ((value & 1) != 0);
        if (m_strobe) {
            m_shift[0] = m_buttons[0];
            m_shift[1] = m_buttons[1];
        }
    }
    /// <summary>Reads a port with its side effect: after the strobe falls, the port shifts to its next button.</summary>
    /// <param name="port">The port, 0 for $4016 or 1 for $4017.</param>
    /// <param name="openBus">The CPU data bus before the read, which supplies bits 5-7.</param>
    /// <returns>The port's byte.</returns>
    public byte Read(int port, byte openBus) {
        var value = Peek(
            openBus: openBus,
            port: port
        );

        if (!m_strobe) {
            m_shift[port & 1] = ((byte)((m_shift[port & 1] >> 1) | 0x80));
        }

        return value;
    }
    /// <summary>Returns what a port read would, changing nothing.</summary>
    /// <param name="port">The port, 0 for $4016 or 1 for $4017.</param>
    /// <param name="openBus">The CPU data bus before the read, which supplies bits 5-7.</param>
    /// <returns>The port's byte.</returns>
    public byte Peek(int port, byte openBus) {
        var bit = (m_strobe ? m_buttons[port & 1] & 1 : m_shift[port & 1] & 1);

        return ((byte)((openBus & 0xE0) | bit));
    }
    /// <inheritdoc/>
    public void SaveState(StateWriter writer) =>
        TransferState(transfer: new StateSaveTransfer(writer: writer));
    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">The reader does not contain the complete port state.</exception>
    public void LoadState(StateReader reader) =>
        TransferState(transfer: new StateLoadTransfer(reader: reader));

    private void TransferState<TTransfer>(TTransfer transfer) where TTransfer : struct, IStateTransfer {
        transfer.Block(values: m_buttons.AsSpan());
        transfer.Block(values: m_shift.AsSpan());
        transfer.Boolean(value: ref m_strobe);
    }
}
