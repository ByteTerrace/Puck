namespace Puck.HumbleGamingBrick.Interfaces;

/// <summary>
/// The interrupt controller: the interrupt-flag register (<c>IF</c>, <c>0xFF0F</c>) and the interrupt-enable
/// register (<c>IE</c>, <c>0xFFFF</c>). Components raise an interrupt by setting its bit in <c>IF</c> via
/// <see cref="Request"/>; the CPU samples the pending set (<c>IE &amp; IF</c>) at instruction boundaries,
/// services the highest-priority source, and clears its bit. The upper three bits of <c>IF</c> have no source
/// and read as set.
/// </summary>
public sealed class InterruptController : IInterruptController {
    private const byte SourceMask = 0x1F;
    private const byte UnusedFlagBits = 0xE0;

    private byte m_interruptEnable;
    private byte m_interruptFlag;

    /// <summary>Gets or sets the interrupt-enable register (<c>IE</c>, <c>0xFFFF</c>). All eight bits are
    /// readable and writable; only the low five gate interrupt service.</summary>
    public byte InterruptEnable {
        get => m_interruptEnable;
        set => m_interruptEnable = value;
    }
    /// <summary>Gets or sets the interrupt-flag register (<c>IF</c>, <c>0xFF0F</c>). Writes keep only the low
    /// five source bits; reads return the unused upper three bits as set.</summary>
    public byte InterruptFlag {
        get => (byte)(m_interruptFlag | UnusedFlagBits);
        set => m_interruptFlag = (byte)(value & SourceMask);
    }
    /// <summary>Gets whether any enabled interrupt is pending — the condition that both dispatches an interrupt
    /// (when the master enable is set) and wakes the CPU from <c>HALT</c> (regardless of the master enable).</summary>
    public bool HasPending =>
        ((m_interruptEnable & m_interruptFlag & SourceMask) != 0);

    /// <summary>Raises an interrupt by setting its bit in <c>IF</c>.</summary>
    /// <param name="kind">The source to request.</param>
    public void Request(InterruptKind kind) =>
        m_interruptFlag |= (byte)((byte)kind & SourceMask);

    /// <summary>Clears an interrupt's bit in <c>IF</c>, as the CPU does when it begins servicing the source.</summary>
    /// <param name="kind">The source to clear.</param>
    public void Clear(InterruptKind kind) =>
        m_interruptFlag &= (byte)~(byte)kind;

    /// <summary>Finds the highest-priority enabled and pending interrupt without clearing it.</summary>
    /// <param name="kind">The highest-priority pending source, or <see cref="InterruptKind.None"/> when none is pending.</param>
    /// <returns><see langword="true"/> when an enabled interrupt is pending; otherwise <see langword="false"/>.</returns>
    public bool TryGetPending(out InterruptKind kind) {
        var pending = (m_interruptEnable & m_interruptFlag & SourceMask);

        if (pending == 0) {
            kind = InterruptKind.None;

            return false;
        }

        // The lowest set bit is the highest priority (VBlank first), matching the hardware service order.
        kind = (InterruptKind)(pending & (~pending + 1));

        return true;
    }
    /// <summary>Returns the interrupt vector address the CPU jumps to when servicing a source.</summary>
    /// <param name="kind">A single interrupt source.</param>
    /// <returns>The handler address: <c>0x40</c> + 8 per bit position (VBlank <c>0x40</c> … Joypad <c>0x60</c>).</returns>
    public static ushort VectorFor(InterruptKind kind) =>
        kind switch {
            InterruptKind.VBlank => 0x40,
            InterruptKind.LcdStat => 0x48,
            InterruptKind.Timer => 0x50,
            InterruptKind.Serial => 0x58,
            InterruptKind.Joypad => 0x60,
            _ => 0x00,
        };
}
