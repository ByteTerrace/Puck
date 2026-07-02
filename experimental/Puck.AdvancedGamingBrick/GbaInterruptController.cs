namespace Puck.AdvancedGamingBrick;

/// <summary>The interrupt controller, ported from ARES's cycle-stepped model (gba/cpu/cpu.cpp <c>stepIRQ</c> +
/// gba/cpu/io.cpp). IE/IF/IME are double-buffered: writes land in the "next" stage [1] and reads return the
/// committed stage [0]; a per-cycle <see cref="StepSync"/> recomputes the synchronizer and shifts [1]→[0]. The
/// 1-cycle register-visibility delay and the 2-cycle timer-overflow→CPU-recognition latency are produced by this
/// pipeline rather than by any tuned constant.</summary>
public sealed class GbaInterruptController : IGbaInterruptController {
    private const ushort SourceMask = 0x3FFF;

    // Two-stage pipeline. [0] = committed/current (what the CPU line and register reads see); [1] = programmed/next
    // (written by software and by peripherals). ARES: irq.enable/flag/ime[0..1], shifted by stepIRQ each cycle.
    private ushort m_enable0;
    private ushort m_enable1;
    private ushort m_flag0;
    private ushort m_flag1;
    private bool m_ime0;
    private bool m_ime1;
    private bool m_synchronizer;

    /// <inheritdoc/>
    public bool Synchronizer => m_synchronizer;

    /// <inheritdoc/>
    public bool HasPendingInterrupt => (m_enable0 & m_flag0 & SourceMask) != 0;

    /// <inheritdoc/>
    public bool PipelineQuiescent =>
        (m_flag0 == m_flag1)
        && (m_enable0 == m_enable1)
        && (m_ime0 == m_ime1)
        && (m_synchronizer == (m_ime0 && ((m_enable0 & m_flag0 & SourceMask) != 0)));

    /// <inheritdoc/>
    public void StepSync(bool stallingCpu) {
        // ARES CPU::stepIRQ (cpu.cpp:63-70): compute the line from the committed stage, then shift the programmed
        // stage down. Frozen while DMA stalls the CPU so a request raised mid-burst surfaces only after it ends.
        if (stallingCpu) {
            return;
        }

        m_synchronizer = m_ime0 && ((m_enable0 & m_flag0 & SourceMask) != 0);
        m_enable0 = m_enable1;
        m_flag0 = m_flag1;
        m_ime0 = m_ime1;
    }

    /// <inheritdoc/>
    public void Request(InterruptSource source) {
        // ARES setInterruptFlag (cpu.cpp:59-61): land the request in the "next" stage.
        m_flag1 |= (ushort)(1u << (int)source);
    }

    /// <inheritdoc/>
    public ushort ReadRegister(uint offset) => offset switch {
        // Reads return the committed [0] stage (ARES io.cpp:187-215) — not the just-written value.
        0x200u => m_enable0,
        0x202u => m_flag0,
        0x208u => (ushort)(m_ime0 ? 1u : 0u),
        _ => 0,
    };

    /// <inheritdoc/>
    public void WriteRegister(uint offset, ushort value) {
        // Writes target the "next" [1] stage (ARES io.cpp:421-446); they take effect after the next StepSync.
        switch (offset) {
            case 0x200u:
                m_enable1 = (ushort)(value & SourceMask);

                break;
            case 0x202u:
                // Writing a one to an IF bit acknowledges (clears) that request, in the next stage.
                m_flag1 &= (ushort)~value;

                break;
            case 0x208u:
                m_ime1 = (value & 1u) != 0u;

                break;
            default:
                break;
        }
    }
}
