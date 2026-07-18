namespace Puck.HumbleGamingBrick;

/// <summary>
/// An infrared link between two machines' <see cref="InfraredPort"/>s, together with the deterministic pair-stepper the
/// medium requires — the IR analogue of <see cref="SerialLinkSession"/>. Constructing the session wires the two
/// transceivers as peers; the pair must then be advanced THROUGH the session: <see cref="Run"/> moves both machines
/// forward by one shared wall-time budget (master-clock T-cycles, i.e. LCD dots — the same unit <see cref="Machine.Run"/>
/// consumes), always stepping the machine that is further behind its own cumulative target, one instruction at a time,
/// with ties going to the first machine. That interleave is a pure function of the two machines' states and the budget
/// sequence — no wall clock, no thread scheduling — so a linked run is deterministic and replay-identical, and the
/// per-machine targets are cumulative (anchored at connect), so instruction overshoot carries between calls instead of
/// accreting into drift.
/// <para>
/// Unlike the serial cable there is NO clock to negotiate: infrared carries a light LEVEL, not a clocked bit stream, so
/// the session does not arbitrate a shift edge — it only keeps the two machines' light states coherent by advancing them
/// in the furthest-behind interleave. The latency model is therefore the same instruction-atomic contract the serial
/// session offers: when a machine samples its received-light line, the peer's emitted light is the peer's state at its
/// last instruction boundary — at most one instruction stale, well inside the many-thousand-cycle windows IR link
/// software (Mystery Gift) times its pulses over. This per-interleave-step propagation IS the deterministic contract;
/// there is no faster-than-one-instruction coupling and none is needed. In a parallel-stepping fleet a linked pair is ONE
/// work item: never step the two machines on separate threads, and never advance either machine directly while the
/// session is live. Disposing the session severs the cable; both machines then step independently again (their received
/// line reverts to dark, as on an unpaired transceiver).
/// </para>
/// <para>
/// Suspend/resume for a credit-preserving reconnect, exactly as the serial session. Because stepping is
/// instruction-atomic, a machine typically ends a budget having overshot its cumulative target by a few cycles; that
/// overshoot is a credit that carries into the next budget. <see cref="Suspend"/> severs the cable and hands back an
/// <see cref="IrLinkResumeToken"/> capturing both credits, and the resume constructor re-anchors each machine's target at
/// <c>CycleCount − credit</c> so a snapshot/restore/reconnect cycle continues the EXACT pacing the suspend severed at. A
/// naive reconnect (the plain constructor) instead discards the credit and diverges the trace by construction — so a
/// linked pair may only be snapshotted across a <see cref="Suspend"/>. IR needs no transfer-idle guard the way the serial
/// cable does (no bit is ever mid-shift — the whole transceiver state is a level plus register bits, all snapshotted), so
/// any budget boundary is a clean severing instant.
/// </para>
/// </summary>
public sealed class IrLinkSession : IDisposable {
    private readonly Machine m_first;
    private readonly InfraredPort m_firstPort;
    private readonly Machine m_second;
    private readonly InfraredPort m_secondPort;
    private bool m_disposed;
    private ulong m_firstTarget;
    private ulong m_secondTarget;

    /// <summary>Connects two machines' infrared transceivers and anchors the pair-stepper at their current instants. The
    /// machines may sit at different points on their own timelines; the cable defines "now" as the moment of connection,
    /// and every subsequent budget advances both by equal wall time from there.</summary>
    /// <param name="first">The first machine (the tie-break winner when both are equally behind).</param>
    /// <param name="second">The second machine.</param>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Both arguments are the same instance.</exception>
    /// <exception cref="InvalidOperationException">Either machine's infrared port is already linked.</exception>
    public IrLinkSession(MachineInstance first, MachineInstance second) {
        ArgumentNullException.ThrowIfNull(argument: first);
        ArgumentNullException.ThrowIfNull(argument: second);

        var firstPort = first.GetRequiredService<InfraredPort>();
        var secondPort = second.GetRequiredService<InfraredPort>();

        InfraredPort.Connect(first: firstPort, second: secondPort);

        m_first = first.Machine;
        m_firstPort = firstPort;
        m_second = second.Machine;
        m_secondPort = secondPort;
        m_firstTarget = m_first.Clock.CycleCount;
        m_secondTarget = m_second.Clock.CycleCount;
    }

    /// <summary>Reconnects a suspended pair, re-anchoring each machine's pacing target at <c>CycleCount − credit</c> so the
    /// run continues the exact pacing the matching <see cref="Suspend"/> severed at — the credit-preserving path a
    /// snapshot/restore/reconnect cycle needs. Use this (never the plain constructor) after restoring both machines from
    /// snapshots taken across a <see cref="Suspend"/>.</summary>
    /// <param name="first">The first machine, restored from its across-suspend snapshot (the tie-break winner).</param>
    /// <param name="second">The second machine, restored from its across-suspend snapshot.</param>
    /// <param name="resumeToken">The token the matching <see cref="Suspend"/> returned.</param>
    /// <exception cref="ArgumentNullException">Either machine is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Both machines are the same instance.</exception>
    /// <exception cref="InvalidOperationException">Either machine's infrared port is already linked.</exception>
    public IrLinkSession(MachineInstance first, MachineInstance second, IrLinkResumeToken resumeToken)
        : this(first: first, second: second) {
        m_firstTarget = (m_first.Clock.CycleCount - resumeToken.FirstCredit);
        m_secondTarget = (m_second.Clock.CycleCount - resumeToken.SecondCredit);
    }

    /// <summary>Advances BOTH machines forward by a shared budget of T-cycles (dots), interleaved deterministically — the
    /// seam a host drives in place of the two machines' own <see cref="Machine.Run"/> while they are linked.</summary>
    /// <param name="tCycles">The number of T-cycles to advance each machine this call.</param>
    /// <exception cref="ObjectDisposedException">The session has been disposed.</exception>
    public void Run(ulong tCycles) {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);

        m_firstTarget += tCycles;
        m_secondTarget += tCycles;

        while (true) {
            var firstRemaining = Remaining(machine: m_first, target: m_firstTarget);
            var secondRemaining = Remaining(machine: m_second, target: m_secondTarget);

            if ((firstRemaining == 0UL) && (secondRemaining == 0UL)) {
                return;
            }

            // Step whichever machine is further behind its target so neither ever observes the other's light more than one
            // instruction stale; the tie goes to the first machine — a fixed, state-free rule, so the interleave (and
            // therefore every light level each side reads) replays identically for identical inputs.
            if (firstRemaining >= secondRemaining) {
                StepOnce(machine: m_first);
            } else {
                StepOnce(machine: m_second);
            }
        }
    }
    /// <summary>Severs the cable and returns the credit token a later credit-preserving reconnect needs. Each machine's
    /// credit is its instruction overshoot at this instant — the T-cycles it has already run past its cumulative link
    /// target (always non-negative: a completed <see cref="Run"/> leaves each machine at or past its target). After suspend
    /// the session is disposed like <see cref="Dispose"/>; both machines then step independently and may be snapshotted,
    /// restored into fresh machines, and reconnected through the resume constructor with this token. Any budget boundary is
    /// a clean severing instant (no bit is ever mid-shift on the IR medium).</summary>
    /// <returns>The token carrying both machines' overshoot credits.</returns>
    /// <exception cref="ObjectDisposedException">The session has already been disposed.</exception>
    public IrLinkResumeToken Suspend() {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);

        var token = new IrLinkResumeToken(
            FirstCredit: (m_first.Clock.CycleCount - m_firstTarget),
            SecondCredit: (m_second.Clock.CycleCount - m_secondTarget)
        );

        Dispose();

        return token;
    }
    /// <summary>Severs the cable: both transceivers lose their peer and the machines step independently again (their
    /// received line reverts to dark). The machines themselves are untouched (they are owned by the caller, not the
    /// session).</summary>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;

        InfraredPort.Disconnect(port: m_firstPort);
        InfraredPort.Disconnect(port: m_secondPort);
    }

    private static ulong Remaining(Machine machine, ulong target) {
        var elapsed = machine.Clock.CycleCount;

        return ((elapsed < target) ? (target - elapsed) : 0UL);
    }
    private static void StepOnce(Machine machine) {
        if (machine.HasBusMaster) {
            machine.StepInstruction();
        } else {
            machine.StepTick();
        }
    }
}
