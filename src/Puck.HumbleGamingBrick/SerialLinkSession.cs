namespace Puck.HumbleGamingBrick;

/// <summary>
/// A serial link cable between two machines, together with the deterministic pair-stepper the cable requires.
/// Constructing the session wires the two machines' serial ports as peers (bits are exchanged synchronously inside the
/// internally-clocked port's tick — see <see cref="SerialComponent"/>); the pair must then be advanced THROUGH the
/// session: <see cref="Run"/> moves both machines forward by one shared wall-time budget (master-clock T-cycles, i.e.
/// LCD dots — the same unit <see cref="Machine.Run"/> consumes, and a rate the DMG/Color models share in every speed
/// mode), always stepping the machine that is further behind its own cumulative target, one instruction at a time,
/// with ties going to the first machine. That interleave is a pure function of the two machines' states and the budget
/// sequence — no wall clock, no thread scheduling — so a linked run is deterministic and replay-identical, and the
/// per-machine targets are cumulative (anchored at connect), so instruction overshoot carries between calls instead of
/// accreting into drift, exactly like <see cref="Machine.Run"/>'s own pacing.
/// <para>
/// Causality across the cable is instruction-atomic: when one machine's serial shifter exchanges a bit, the peer's
/// state is its last instruction boundary — at most one instruction stale, well inside a normal-rate bit period
/// (512 T-cycles). That is the finest an instruction-atomic core can offer; byte-level link protocols (the handshake
/// style real link software uses) are exact under it. In a parallel-stepping fleet, a linked pair is ONE work item:
/// never step the two machines on separate threads, and never advance either machine directly while the session is
/// live. Disposing the session severs the cable; both machines then step independently again (an unfinished
/// external-clock transfer stays pending, as on unplugged hardware).
/// </para>
/// <para>
/// Suspend/resume for a credit-preserving reconnect. Because stepping is instruction-atomic, a machine typically ends a
/// budget having overshot its cumulative target by a few cycles; that overshoot is a credit that carries into the next
/// budget. <see cref="Suspend"/> severs the cable and hands back a <see cref="SerialLinkResumeToken"/> capturing both
/// credits, and the resume constructor re-anchors each machine's target at <c>CycleCount − credit</c> so a
/// snapshot/restore/reconnect cycle continues the EXACT pacing the suspend severed at. A naive reconnect (the plain
/// constructor, which anchors targets at the current instant) instead discards the credit and runs those extra cycles,
/// diverging the trace by construction — so a linked pair may only be snapshotted across a <see cref="Suspend"/>, and
/// only at a transfer-idle instant.
/// </para>
/// </summary>
public sealed class SerialLinkSession : IDisposable {
    private readonly Machine m_first;
    private readonly SerialComponent m_firstPort;
    private readonly Machine m_second;
    private readonly SerialComponent m_secondPort;
    private bool m_disposed;
    private ulong m_firstTarget;
    private ulong m_secondTarget;

    /// <summary>Connects two machines' serial ports and anchors the pair-stepper at their current instants. The
    /// machines may sit at different points on their own timelines (one may have booted long before the other); the
    /// cable defines "now" as the moment of connection, and every subsequent budget advances both by equal wall
    /// time from there.</summary>
    /// <param name="first">The first machine (the tie-break winner when both are equally behind).</param>
    /// <param name="second">The second machine.</param>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Both arguments are the same instance.</exception>
    /// <exception cref="InvalidOperationException">Either machine's serial port is already linked.</exception>
    public SerialLinkSession(MachineInstance first, MachineInstance second) {
        ArgumentNullException.ThrowIfNull(argument: first);
        ArgumentNullException.ThrowIfNull(argument: second);

        var firstPort = first.GetRequiredService<SerialComponent>();
        var secondPort = second.GetRequiredService<SerialComponent>();

        SerialComponent.Connect(first: firstPort, second: secondPort);

        m_first = first.Machine;
        m_firstPort = firstPort;
        m_second = second.Machine;
        m_secondPort = secondPort;
        m_firstTarget = m_first.Clock.CycleCount;
        m_secondTarget = m_second.Clock.CycleCount;
    }

    /// <summary>Reconnects a suspended pair, re-anchoring each machine's pacing target at <c>CycleCount − credit</c> so
    /// the run continues the exact pacing the matching <see cref="Suspend"/> severed at — the credit-preserving path a
    /// snapshot/restore/reconnect cycle needs. Use this (never the plain constructor) after restoring both machines from
    /// snapshots taken across a <see cref="Suspend"/>.</summary>
    /// <param name="first">The first machine, restored from its across-suspend snapshot (the tie-break winner).</param>
    /// <param name="second">The second machine, restored from its across-suspend snapshot.</param>
    /// <param name="resumeToken">The token the matching <see cref="Suspend"/> returned.</param>
    /// <exception cref="ArgumentNullException">Either machine is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Both machines are the same instance.</exception>
    /// <exception cref="InvalidOperationException">Either machine's serial port is already linked.</exception>
    public SerialLinkSession(MachineInstance first, MachineInstance second, SerialLinkResumeToken resumeToken)
        : this(first: first, second: second) {
        m_firstTarget = (m_first.Clock.CycleCount - resumeToken.FirstCredit);
        m_secondTarget = (m_second.Clock.CycleCount - resumeToken.SecondCredit);
    }

    /// <summary>Advances BOTH machines forward by a shared budget of T-cycles (dots), interleaved deterministically —
    /// the seam a host drives in place of the two machines' own <see cref="Machine.Run"/> while they are linked.</summary>
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

            // Step whichever machine is further behind its target so neither ever observes the other more than one
            // instruction stale; the tie goes to the first machine — a fixed, state-free rule, so the interleave (and
            // therefore every bit exchanged) replays identically for identical inputs.
            if (firstRemaining >= secondRemaining) {
                StepOnce(machine: m_first);
            } else {
                StepOnce(machine: m_second);
            }
        }
    }
    /// <summary>Severs the cable and returns the credit token a later credit-preserving reconnect needs. Each machine's
    /// credit is its instruction overshoot at this instant — the T-cycles it has already run past its cumulative link
    /// target (always non-negative: a completed <see cref="Run"/> leaves each machine at or past its target). After
    /// suspend the session is disposed like <see cref="Dispose"/>; both machines then step independently and may be
    /// snapshotted, restored into fresh machines, and reconnected through the resume constructor with this token.
    /// Suspend only at a transfer-idle instant (no transfer bit set on either port), never mid-transfer.</summary>
    /// <returns>The token carrying both machines' overshoot credits.</returns>
    /// <exception cref="ObjectDisposedException">The session has already been disposed.</exception>
    public SerialLinkResumeToken Suspend() {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);

        var token = new SerialLinkResumeToken(
            FirstCredit: (m_first.Clock.CycleCount - m_firstTarget),
            SecondCredit: (m_second.Clock.CycleCount - m_secondTarget)
        );

        Dispose();

        return token;
    }
    /// <summary>Severs the cable: both serial ports lose their peer and the machines step independently again. The
    /// machines themselves are untouched (they are owned by the caller, not the session).</summary>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;

        SerialComponent.Disconnect(port: m_firstPort);
        SerialComponent.Disconnect(port: m_secondPort);
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
