namespace Puck.GameBoy;

/// <summary>
/// The OAM DMA engine. A write to <c>0xFF46</c> starts a transfer of 160 bytes from the page
/// <c>value &#215; 0x100</c> into object-attribute memory, one byte per machine cycle, after a one-cycle setup
/// delay. While a transfer is in flight, object-attribute memory is inaccessible to the CPU and PPU (reads
/// return <c>0xFF</c>, writes are dropped), which is why games kick off the copy during vertical blank from a
/// routine running in high RAM.
/// </summary>
public sealed class OamDma : IOamDma {
    private const int ByteCount = 0xA0;
    private const int StartupDelayMachineCycles = 1;
    private const int TCyclesPerMachineCycle = 4;

    private readonly byte[] m_oam;

    private bool m_active;
    private bool m_locked;
    private bool m_pendingUnlock;
    private int m_index;
    private byte m_page;
    private int m_startupDelay;
    private int m_tCycleAccumulator;

    /// <inheritdoc />
    public ClockDomain Domain =>
        ClockDomain.Cpu;
    /// <summary>Gets whether a transfer is scheduled or running.</summary>
    public bool IsActive =>
        m_active;
    /// <summary>Gets whether object-attribute memory is currently inaccessible to the CPU and PPU. This asserts
    /// only once the transfer is actually copying (after the one-cycle setup delay), so OAM stays readable during
    /// the setup cycle of a fresh transfer; a transfer restarted over one already in flight keeps the lock held.</summary>
    public bool IsOamLocked =>
        m_locked;
    /// <summary>Gets the most recently written source page, as read back from <c>0xFF46</c>.</summary>
    public byte Page =>
        m_page;
    /// <inheritdoc />
    public Func<ushort, byte> ReadSource { get; set; } = static _ => (byte)0xFF;

    /// <summary>Initializes the engine over the shared object attribute memory it fills.</summary>
    /// <param name="memory">The shared system memory whose object attribute memory the transfer writes into.</param>
    /// <exception cref="ArgumentNullException"><paramref name="memory"/> is <see langword="null"/>.</exception>
    public OamDma(SystemMemory memory) {
        ArgumentNullException.ThrowIfNull(memory);

        m_oam = memory.ObjectAttributeMemory;
    }

    /// <summary>Starts (or restarts) a transfer from the given source page.</summary>
    /// <param name="page">The high byte of the source address; the transfer reads <c>page &#215; 0x100</c> onward.</param>
    public void Start(byte page) {
        // The lock latch is intentionally left as-is: a fresh transfer keeps OAM readable through its setup
        // delay, while restarting over an in-flight transfer keeps the existing lock asserted. Any pending
        // post-transfer unlock is cancelled so a restart does not drop the lock.
        m_active = true;
        m_index = 0;
        m_page = page;
        m_pendingUnlock = false;
        m_startupDelay = StartupDelayMachineCycles;
        m_tCycleAccumulator = 0;
    }

    /// <inheritdoc />
    public void Step(int tCycles) {
        m_tCycleAccumulator += tCycles;

        // The transfer advances one byte per machine cycle (four CPU-domain T-cycles).
        while (m_tCycleAccumulator >= TCyclesPerMachineCycle) {
            m_tCycleAccumulator -= TCyclesPerMachineCycle;
            StepMachineCycle();
        }
    }

    private void StepMachineCycle() {
        // The lock outlives the final byte copy by one machine cycle: OAM stays inaccessible on the cycle the
        // last byte is written and only becomes readable the cycle after, which is the hardware-observed end.
        if (m_pendingUnlock) {
            m_pendingUnlock = false;
            m_locked = false;

            return;
        }

        if (!m_active) {
            return;
        }

        if (m_startupDelay > 0) {
            m_startupDelay -= 1;

            return;
        }

        // The transfer is now driving the bus; OAM becomes inaccessible from here until it completes.
        m_locked = true;
        m_oam[m_index] = ReadSource(arg: (ushort)((m_page << 8) + m_index));
        m_index += 1;

        if (m_index >= ByteCount) {
            m_active = false;
            m_pendingUnlock = true;
        }
    }
}
