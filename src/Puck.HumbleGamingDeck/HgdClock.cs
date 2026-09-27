namespace Puck.HumbleGamingDeck;

/// <summary>The current half of a CPU cycle on the reference master clock.</summary>
public enum HgdCpuSubphase {
    /// <summary>M2 low: the CPU prepares its next transaction.</summary>
    M2Low,
    /// <summary>M2 high: the CPU transaction completes at the falling edge.</summary>
    M2High,
}
/// <summary>A transition of the CPU's M2 signal at a master-tick boundary.</summary>
public enum HgdM2Edge {
    /// <summary>The M2 level does not change.</summary>
    None,
    /// <summary>M2 changes from low to high.</summary>
    Rising,
    /// <summary>M2 changes from high to low.</summary>
    Falling,
}
/// <summary>Master ticks, the CPU divider, and the accumulated run target, serialized as one timing component.</summary>
public sealed class HgdClock : ISnapshotable {
    private readonly int m_cpuDivider;

    private ulong m_masterTicks;
    private ulong m_runTargetCycles;
    private int m_cpuPhase;

    /// <summary>Initializes a new instance of the <see cref="HgdClock"/> class at the configured alignment.</summary>
    /// <param name="configuration">The model and phase profile.</param>
    /// <exception cref="ArgumentNullException"><paramref name="configuration"/> is <see langword="null"/>.</exception>
    public HgdClock(HgdMachineConfiguration configuration) {
        ArgumentNullException.ThrowIfNull(argument: configuration);

        m_cpuDivider = configuration.Model.CpuDivider();
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
    /// <summary>Gets the explicit CPU half-cycle.</summary>
    public HgdCpuSubphase Subphase => ((m_cpuPhase < (m_cpuDivider / 2)) ? HgdCpuSubphase.M2Low : HgdCpuSubphase.M2High);

    /// <summary>Adds a host budget while preserving previously accumulated overshoot.</summary>
    /// <param name="masterTicks">The additional master ticks to request.</param>
    /// <exception cref="OverflowException">The target exceeds the clock's range.</exception>
    public void AddBudget(ulong masterTicks) {
        m_runTargetCycles = checked((m_runTargetCycles + masterTicks));
    }
    /// <summary>Advances exactly one master tick and reports CPU divider edges.</summary>
    /// <returns>The M2 transition, or <see cref="HgdM2Edge.None"/> between edges.</returns>
    public HgdM2Edge StepTick() {
        ++m_masterTicks;
        ++m_cpuPhase;
        if (m_cpuPhase == m_cpuDivider) {
            m_cpuPhase = 0;

            return HgdM2Edge.Falling;
        }

        return ((m_cpuPhase == (m_cpuDivider / 2)) ? HgdM2Edge.Rising : HgdM2Edge.None);
    }
    /// <inheritdoc/>
    public void SaveState(StateWriter writer) {
        TransferState(transfer: new StateSaveTransfer(writer: writer));
    }
    /// <inheritdoc/>
    /// <exception cref="InvalidDataException">The serialized phase or pacing target is invalid.</exception>
    /// <exception cref="InvalidOperationException">The reader does not contain a complete clock state.</exception>
    public void LoadState(StateReader reader) {
        TransferState(transfer: new StateLoadTransfer(reader: reader));
        if ((((uint)m_cpuPhase) >= ((uint)m_cpuDivider)) || (m_runTargetCycles > m_masterTicks)) {
            throw new InvalidDataException(message: "Snapshot clock phase or pacing target is invalid.");
        }
    }

    private void TransferState<TTransfer>(TTransfer transfer) where TTransfer : struct, IStateTransfer {
        transfer.UInt64(value: ref m_masterTicks);
        transfer.UInt64(value: ref m_runTargetCycles);
        transfer.Int32(value: ref m_cpuPhase);
    }
}
