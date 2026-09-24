namespace Puck.HumbleGamingBrick;

/// <summary>
/// A link between two machines over one medium, together with the deterministic pair-stepper every medium requires.
/// Constructing a session wires the two machines' <typeparamref name="TPort"/> components as peers; the pair must then
/// be advanced through the session: <see cref="Run"/> moves both machines forward by one shared wall-time budget
/// (master-clock T-cycles, i.e. LCD dots — the same unit <see cref="Machine.Run"/> consumes, and a rate the DMG/Color
/// models share in every speed mode), always stepping the machine that is further behind its own cumulative target,
/// one instruction at a time, with ties going to the first machine. That interleave is the pair case of the shared
/// <see cref="LinkPacer"/>, a pure function of the two machines' states and the budget sequence — no wall clock, no
/// thread scheduling — so a linked run is deterministic and replay-identical. The per-machine targets are cumulative
/// (anchored at connect), so instruction overshoot carries between calls instead of accreting into drift, exactly like
/// <see cref="Machine.Run"/>'s own pacing.
/// <para>
/// Causality across the medium is instruction-atomic: when one machine observes the medium, the peer's state is its
/// last instruction boundary, at most one instruction stale. In a parallel-stepping fleet a linked pair is one work
/// item: never step the two machines on separate threads, and never advance either machine directly while the session
/// is live. Disposing the session severs the link; both machines then step independently again.
/// </para>
/// <para>
/// <b>Pacing credits.</b> Because stepping is instruction-atomic, a machine typically ends a budget having overshot
/// its cumulative target by a few cycles; that overshoot is a credit that carries into the next budget. There are two
/// ways to carry it across a snapshot. <see cref="Suspend"/> severs the link and hands back a
/// <see cref="LinkResumeToken"/> capturing both credits, and the resume constructor re-anchors each machine's target at
/// <c>CycleCount − credit</c>, so a snapshot/restore/reconnect cycle continues the exact pacing the suspend severed at.
/// A live session that never severs reads the same credits through <see cref="PacingCredits"/> and re-anchors them
/// through <see cref="ReanchorPacing"/>, the path a coupled rewind takes, where the link stays wired and both machines
/// are restored in place. A naive reconnect (the plain constructor, which anchors targets at the current instant)
/// instead discards the credit and runs those extra cycles, diverging the trace by construction.
/// </para>
/// </summary>
/// <typeparam name="TPort">The medium's port component, resolved from each machine's services.</typeparam>
public abstract class LinkSession<TPort> : IDisposable where TPort : class {
    private readonly Action<TPort> m_disconnect;
    private readonly Machine m_first;
    private readonly TPort m_firstPort;
    private readonly Machine m_second;
    private readonly TPort m_secondPort;

    private bool m_disposed;
    private ulong m_firstTarget;
    private ulong m_secondTarget;

    /// <summary>Initializes a new instance of the <see cref="LinkSession{TPort}"/> class, connecting two machines'
    /// ports and anchoring the pair-stepper at their current instants. The machines may sit at different points on their
    /// own timelines; the link defines "now" as the moment of connection, and every subsequent budget advances both by
    /// equal wall time from there.</summary>
    /// <param name="first">The first machine (the tie-break winner when both are equally behind).</param>
    /// <param name="second">The second machine.</param>
    /// <param name="connect">The medium's peer-wiring seam, which refuses a port that is already linked.</param>
    /// <param name="disconnect">The medium's severing seam, called once per port on disposal.</param>
    /// <exception cref="ArgumentNullException">Either machine is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Both machines are the same instance.</exception>
    /// <exception cref="InvalidOperationException">Either machine's port is already linked.</exception>
    private protected LinkSession(MachineInstance first, MachineInstance second, Action<TPort, TPort> connect, Action<TPort> disconnect) {
        ArgumentNullException.ThrowIfNull(argument: first);
        ArgumentNullException.ThrowIfNull(argument: second);

        var firstPort = first.GetRequiredService<TPort>();
        var secondPort = second.GetRequiredService<TPort>();

        connect(
            arg1: firstPort,
            arg2: secondPort
        );

        m_disconnect = disconnect;
        m_first = first.Machine;
        m_firstPort = firstPort;
        m_firstTarget = m_first.Clock.CycleCount;
        m_second = second.Machine;
        m_secondPort = secondPort;
        m_secondTarget = m_second.Clock.CycleCount;
    }
    /// <summary>Initializes a new instance of the <see cref="LinkSession{TPort}"/> class for a suspended pair,
    /// re-anchoring each machine's pacing target at <c>CycleCount − credit</c> so the run continues the exact pacing the
    /// matching <see cref="Suspend"/> severed at. Use this (never the plain constructor) after restoring both machines
    /// from snapshots taken across a <see cref="Suspend"/>.</summary>
    /// <param name="first">The first machine, restored from its across-suspend snapshot (the tie-break winner).</param>
    /// <param name="second">The second machine, restored from its across-suspend snapshot.</param>
    /// <param name="resumeToken">The token the matching <see cref="Suspend"/> returned.</param>
    /// <param name="connect">The medium's peer-wiring seam, which refuses a port that is already linked.</param>
    /// <param name="disconnect">The medium's severing seam, called once per port on disposal.</param>
    /// <exception cref="ArgumentNullException">Either machine is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Both machines are the same instance, or a credit exceeds its machine's own
    /// cycle count — a token that does not fit either machine, the signature a reordered, substituted, or otherwise
    /// corrupted token leaves behind.</exception>
    /// <exception cref="InvalidOperationException">Either machine's port is already linked.</exception>
    private protected LinkSession(MachineInstance first, MachineInstance second, LinkResumeToken resumeToken, Action<TPort, TPort> connect, Action<TPort> disconnect)
        : this(
        // Validated before either port is connected: an oversized credit must fail here, never after the pair is
        // already wired (there is no seam left to disconnect it through once construction has thrown).
        connect: connect,
        disconnect: disconnect,
        first: RequireCreditFits(
            credit: resumeToken.FirstCredit,
            instance: first,
            side: "first"
        ),
        second: RequireCreditFits(
            credit: resumeToken.SecondCredit,
            instance: second,
            side: "second"
        )
    ) =>
        Reanchor(credits: resumeToken);

    /// <summary>Gets the pair-stepper's live pacing state as each machine's instruction-overshoot credit — the same
    /// quantity <see cref="Suspend"/> hands back, readable at any instant without severing the link. A link that
    /// snapshots its members must snapshot this beside them: it is the session's own state, not either machine's, so a
    /// restore that reproduces both machines but re-anchors the targets at the landing instant discards the credit and
    /// diverges the trace by construction.</summary>
    public LinkResumeToken PacingCredits =>
        new(
            FirstCredit: (m_first.Clock.CycleCount - m_firstTarget),
            SecondCredit: (m_second.Clock.CycleCount - m_secondTarget)
        );

    // Checks that a credit fits inside a machine's own cycle count: CycleCount − credit is unsigned and would otherwise
    // wrap to a target billions of cycles away.
    private static void RequireCreditFits(ulong cycleCount, ulong credit, string side) {
        if (credit > cycleCount) {
            throw new ArgumentException(
                message: $"the resume token's {side} credit ({credit}) exceeds the machine's cycle count ({cycleCount}).",
                paramName: "resumeToken"
            );
        }
    }
    // The constructor-initializer form: returns the instance so the check runs before the plain constructor connects
    // either port.
    private static MachineInstance RequireCreditFits(MachineInstance instance, ulong credit, string side) {
        ArgumentNullException.ThrowIfNull(argument: instance);

        RequireCreditFits(
            credit: credit,
            cycleCount: instance.Machine.Clock.CycleCount,
            side: side
        );

        return instance;
    }
    private void Reanchor(LinkResumeToken credits) {
        RequireCreditFits(
            credit: credits.FirstCredit,
            cycleCount: m_first.Clock.CycleCount,
            side: "first"
        );
        RequireCreditFits(
            credit: credits.SecondCredit,
            cycleCount: m_second.Clock.CycleCount,
            side: "second"
        );

        m_firstTarget = (m_first.Clock.CycleCount - credits.FirstCredit);
        m_secondTarget = (m_second.Clock.CycleCount - credits.SecondCredit);
    }

    /// <summary>Refuses a suspend at an instant the medium cannot be severed and later resumed from. The default accepts
    /// every budget boundary.</summary>
    /// <param name="firstPort">The first machine's port.</param>
    /// <param name="secondPort">The second machine's port.</param>
    /// <exception cref="InvalidOperationException">The medium is mid-exchange at this instant.</exception>
    private protected virtual void RequireSeverable(TPort firstPort, TPort secondPort) { }

    /// <summary>Severs the link: both ports lose their peer and the machines step independently again. The machines
    /// themselves are untouched (they are owned by the caller, not the session).</summary>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;

        m_disconnect(obj: m_firstPort);
        m_disconnect(obj: m_secondPort);
        GC.SuppressFinalize(obj: this);
    }
    /// <summary>Re-anchors both pacing targets at <c>CycleCount − credit</c> without touching the link — the
    /// restore-side counterpart of <see cref="PacingCredits"/>, for a link that has just restored both machines into
    /// the live session rather than reconnecting fresh ones. There is no transfer-idle requirement: an in-flight
    /// exchange is part of each machine's own snapshot, so a mid-exchange instant restores exactly.</summary>
    /// <param name="credits">The credits captured beside the machines' states.</param>
    /// <exception cref="ObjectDisposedException">The session has been disposed.</exception>
    /// <exception cref="ArgumentException">A credit exceeds its machine's own cycle count.</exception>
    public void ReanchorPacing(LinkResumeToken credits) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );
        Reanchor(credits: credits);
    }
    /// <summary>Advances both machines forward by a shared budget of T-cycles (dots), interleaved deterministically —
    /// the seam a host drives in place of the two machines' own <see cref="Machine.Run"/> while they are linked. Both
    /// targets move by <paramref name="tCycles"/>, then each machine steps until it reaches its own.</summary>
    /// <param name="tCycles">The number of T-cycles to advance each machine this call.</param>
    /// <exception cref="ObjectDisposedException">The session has been disposed.</exception>
    public void Run(ulong tCycles) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        m_firstTarget += tCycles;
        m_secondTarget += tCycles;

        LinkPacer.Run(participants: new Pair(
            first: m_first,
            firstTarget: m_firstTarget,
            second: m_second,
            secondTarget: m_secondTarget
        ));
    }
    /// <summary>Severs the link and returns the credit token a later credit-preserving reconnect needs. Each machine's
    /// credit is its instruction overshoot at this instant — the T-cycles it has already run past its cumulative link
    /// target (always non-negative: a completed <see cref="Run"/> leaves each machine at or past its target). After
    /// suspend the session is disposed like <see cref="Dispose"/>; both machines then step independently and may be
    /// snapshotted, restored into fresh machines, and reconnected through the resume constructor with this token. A
    /// medium that cannot be severed mid-exchange refuses the suspend and leaves the link wired.</summary>
    /// <returns>The token carrying both machines' overshoot credits.</returns>
    /// <exception cref="ObjectDisposedException">The session has already been disposed.</exception>
    /// <exception cref="InvalidOperationException">The medium is mid-exchange at this instant.</exception>
    public LinkResumeToken Suspend() {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );
        RequireSeverable(
            firstPort: m_firstPort,
            secondPort: m_secondPort
        );

        var token = PacingCredits;

        Dispose();

        return token;
    }

    // The pair as the shared pacer sees it. Cycle counts and targets are unsigned here, so an overshoot is reported as
    // a plain zero remainder rather than an unsigned subtraction wrapping into a target billions of cycles away.
    private readonly struct Pair(Machine first, ulong firstTarget, Machine second, ulong secondTarget) : ILinkPacerParticipants {
        public int Count =>
            2;

        public long GetRemaining(int index) {
            var machine = ((index == 0)
                ? first
                : second
            );
            var target = ((index == 0)
                ? firstTarget
                : secondTarget
            );
            var elapsed = machine.Clock.CycleCount;

            return ((elapsed < target)
                ? ((long)(target - elapsed))
                : 0L
            );
        }
        public void StepOnce(int index) {
            var machine = ((index == 0)
                ? first
                : second
            );

            if (machine.HasBusMaster) {
                machine.StepInstruction();
            } else {
                machine.StepTick();
            }
        }
    }
}
