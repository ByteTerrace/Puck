namespace Puck.HumbleGamingDeck;

/// <summary>The current half of a CPU cycle on the reference master clock.</summary>
public enum HgdCpuSubphase {
    /// <summary>The internal first half-cycle, when the CPU prepares its next transaction.</summary>
    Phi1,
    /// <summary>The internal second half-cycle, when the CPU completes its transaction.</summary>
    Phi2,
}
/// <summary>A transition of the CPU's M2 signal within a master tick.</summary>
public enum HgdM2Edge {
    /// <summary>The M2 level does not change.</summary>
    None,
    /// <summary>M2 changes from low to high.</summary>
    Rising,
    /// <summary>M2 changes from high to low.</summary>
    Falling,
}
/// <summary>Master ticks, the CPU and PPU dividers, and the accumulated run target, serialized as one timing
/// component. The PPU's divider starts at phase 0 and the CPU's at the power-on profile's alignment phase, which is how
/// the four CPU/PPU alignments differ.</summary>
public sealed class HgdClock : ISnapshotable {
    private readonly int m_cpuDivider;
    private readonly int m_m2RiseHalfTick;
    private readonly int m_ppuDivider;

    private ulong m_masterTicks;
    private ulong m_runTargetCycles;
    private int m_cpuPhase;
    private int m_ppuPhase;

    /// <summary>Initializes a new instance of the <see cref="HgdClock"/> class at the configured alignment.</summary>
    /// <param name="configuration">The model and phase profile.</param>
    /// <exception cref="ArgumentNullException"><paramref name="configuration"/> is <see langword="null"/>.</exception>
    public HgdClock(HgdMachineConfiguration configuration) {
        ArgumentNullException.ThrowIfNull(argument: configuration);

        m_cpuDivider = configuration.Model.CpuDivider();
        m_m2RiseHalfTick = configuration.Model.M2RiseHalfTick();
        m_ppuDivider = configuration.Model.PpuDivider();
        m_cpuPhase = configuration.PowerOn.AlignmentPhase;
        Rate = configuration.Model.MasterClockRate();
    }

    /// <summary>Gets the completed master ticks.</summary>
    public ulong MasterTicks => m_masterTicks;
    /// <summary>Gets the accumulated run target; an instruction step may leave the clock beyond it.</summary>
    public ulong RunTargetCycles => m_runTargetCycles;
    /// <summary>Gets the integer master-tick rate.</summary>
    public MachineCycleRate Rate {
        get;
    }
    /// <summary>Gets the master-tick phase within the CPU divider.</summary>
    public int CpuPhase => m_cpuPhase;
    /// <summary>Gets the internal CPU half-cycle, distinct from the cartridge's M2 signal.</summary>
    public HgdCpuSubphase Subphase => ((m_cpuPhase < (m_cpuDivider / 2)) ? HgdCpuSubphase.Phi1 : HgdCpuSubphase.Phi2);

    /// <summary>Adds a host budget while preserving previously accumulated overshoot.</summary>
    /// <param name="masterTicks">The additional master ticks to request.</param>
    /// <exception cref="OverflowException">The target exceeds the clock's range.</exception>
    public void AddBudget(ulong masterTicks) {
        m_runTargetCycles = checked((m_runTargetCycles + masterTicks));
    }
    /// <summary>Advances exactly one master tick and reports whether it ends a PPU dot and any M2 edge within it.</summary>
    /// <param name="ppuDot">Whether the tick completes a PPU dot.</param>
    /// <param name="edgeHalfTick">The edge's elapsed time in half master ticks, or zero when there is no edge.</param>
    /// <returns>The M2 transition, or <see cref="HgdM2Edge.None"/> between edges.</returns>
    public HgdM2Edge StepTick(out bool ppuDot, out ulong edgeHalfTick) {
        edgeHalfTick = 0;
        ++m_masterTicks;
        ++m_ppuPhase;
        ppuDot = (m_ppuPhase == m_ppuDivider);
        if (ppuDot) {
            m_ppuPhase = 0;
        }
        ++m_cpuPhase;
        if (m_cpuPhase == m_cpuDivider) {
            m_cpuPhase = 0;
            edgeHalfTick = (m_masterTicks * 2);

            return HgdM2Edge.Falling;
        }

        if (m_cpuPhase == ((m_m2RiseHalfTick + 1) / 2)) {
            edgeHalfTick = ((m_masterTicks * 2) - ((ulong)(m_m2RiseHalfTick & 1)));

            return HgdM2Edge.Rising;
        }

        return HgdM2Edge.None;
    }
    /// <inheritdoc/>
    public void SaveState(StateWriter writer) {
        TransferState(transfer: new StateSaveTransfer(writer: writer));
    }
    /// <inheritdoc/>
    /// <exception cref="InvalidDataException">A serialized phase is outside its divider.</exception>
    /// <exception cref="InvalidOperationException">The reader does not contain a complete clock state.</exception>
    public void LoadState(StateReader reader) {
        TransferState(transfer: new StateLoadTransfer(reader: reader));
        if ((((uint)m_cpuPhase) >= ((uint)m_cpuDivider)) || (((uint)m_ppuPhase) >= ((uint)m_ppuDivider))) {
            throw new InvalidDataException(message: "Snapshot clock phase is outside the CPU or PPU divider.");
        }
    }

    private void TransferState<TTransfer>(TTransfer transfer) where TTransfer : struct, IStateTransfer {
        transfer.UInt64(value: ref m_masterTicks);
        transfer.UInt64(value: ref m_runTargetCycles);
        transfer.Int32(value: ref m_cpuPhase);
        transfer.Int32(value: ref m_ppuPhase);
    }
}
