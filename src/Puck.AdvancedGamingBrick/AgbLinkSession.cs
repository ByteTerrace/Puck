namespace Puck.AdvancedGamingBrick;

/// <summary>
/// A link-cable session between two to four machines, together with the deterministic interleave the cable requires.
/// Constructing the session builds an <see cref="AgbLinkCable"/> and connects each machine's serial controller in
/// argument order (the first machine is the parent, player 0); the set must then be advanced THROUGH the session:
/// <see cref="Run"/> moves every machine forward by one shared budget of master-clock cycles (the same unit
/// <see cref="AdvancedGamingBrickMachine.RunCycles"/> consumes), always stepping the machine that is furthest behind
/// its own cumulative target, one instruction at a time, ties to the lowest index. That interleave is a pure function
/// of the machines' states and the budget sequence — a fixed, state-free rule, the same one the DMG/CGB
/// <c>SerialLinkSession</c> proved — so a linked run is deterministic and replay-identical, and the per-machine
/// targets are cumulative (anchored at connect), so instruction overshoot carries between calls instead of accreting
/// into drift.
/// <para>
/// Causality across the cable is instruction-atomic: when one machine's transfer completes and the cable exchanges
/// words, each peer's state is its last instruction boundary — at most one instruction stale, thousands of cycles
/// inside even the fastest multiplayer round. In a parallel-stepping fleet a linked set is ONE work item: never step
/// the machines on separate threads, and never advance any of them directly while the session is live. The cable
/// itself holds no emulated state (every observable value lives in the consoles' serial registers), so each machine's
/// own snapshot still captures the linked world completely; link topology is re-created by constructing a fresh
/// session, never restored from a snapshot. Disposing the session severs the cable: every controller reverts to the
/// lone-console <see cref="NullAgbLink"/> and the machines — owned by the caller, not the session — step
/// independently again, an unfinished transfer left pending as on unplugged hardware.
/// </para>
/// <para>
/// Suspend/resume for a credit-preserving reconnect. Because stepping is instruction-atomic, a console typically ends
/// a budget having overshot its cumulative target by a few cycles; that overshoot is a credit that carries into the
/// next budget. <see cref="Suspend"/> requires every console to be transfer-idle (SIOCNT's start/busy bit clear on
/// all of them) — it throws <see cref="InvalidOperationException"/> rather than severing a cable mid-transfer, since
/// a round no console can recover would leave the resumed session unable to reconstruct hardware state. On success it
/// severs the cable and hands back an <see cref="AgbLinkResumeToken"/> capturing every console's credit AND identity
/// (format version / BIOS / ROM fingerprint), and the resume constructor validates that binding before re-anchoring
/// each console's target at <c>Cycles − credit</c>, so a snapshot/restore/reconnect cycle continues the EXACT pacing
/// the suspend severed at — on the same consoles, in the same order. The plain constructor instead anchors targets at
/// the current instant, discarding the credit and running those extra cycles — diverging the trace by construction —
/// so a linked set may only be snapshotted across a <see cref="Suspend"/>.
/// </para>
/// </summary>
public sealed class AgbLinkSession : IDisposable {
    private readonly IAgbSerialController[] m_controllers;
    private readonly AdvancedGamingBrickMachine[] m_machines;
    private readonly long[] m_targets;
    private bool m_disposed;

    /// <summary>Connects the consoles' serial controllers into one cable, in order (the first console is the parent,
    /// player 0), and anchors the interleave at their current instants. The consoles may sit at different points on
    /// their own timelines; the cable defines "now" as the moment of connection, and every subsequent budget advances
    /// all of them by equal emulated time from there.</summary>
    /// <param name="consoles">The consoles to join, 2 to <see cref="AgbLinkCable.MaxPlayers"/> of them.</param>
    /// <exception cref="ArgumentNullException"><paramref name="consoles"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The count is out of range, an element is <see langword="null"/>, or the
    /// same console appears twice. No console is connected when this is thrown.</exception>
    public AgbLinkSession(params AgbMachineInstance[] consoles) : this(plan: BuildFreshPlan(consoles: consoles)) {
    }

    /// <summary>Reconnects a suspended set, re-anchoring each console's pacing target at <c>Cycles − credit</c> so the
    /// run continues the exact pacing the matching <see cref="Suspend"/> severed at — the credit-preserving path a
    /// snapshot/restore/reconnect cycle needs. Use this (never the plain constructor) after restoring every console
    /// from its across-suspend snapshot, in the SAME order they were passed to the suspended session.</summary>
    /// <param name="resumeToken">The token the matching <see cref="Suspend"/> returned.</param>
    /// <param name="consoles">The consoles to join, restored from their across-suspend snapshots, in the original
    /// order.</param>
    /// <exception cref="ArgumentNullException"><paramref name="resumeToken"/> or <paramref name="consoles"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The count is out of range, an element is <see langword="null"/>, the same
    /// console appears twice, the resume token's credit count does not match <paramref name="consoles"/>, a credit is
    /// negative, or a console's identity (format version / BIOS / ROM) does not match the token's binding for that
    /// position — a reordered or substituted console. No console is connected when this is thrown.</exception>
    public AgbLinkSession(AgbLinkResumeToken resumeToken, params AgbMachineInstance[] consoles)
        : this(plan: BuildResumePlan(resumeToken: resumeToken, consoles: consoles)) {
    }

    // Wires the cable from an already-validated plan. Every check — console shape, resume-token credit count/values,
    // and console identity binding — has already passed by the time this body runs (it only executes after the
    // constructor-initializer argument above returns successfully), so construction either connects every console or
    // throws before touching any of them, never partially.
    private AgbLinkSession(LinkPlan plan) {
        var cable = new AgbLinkCable();

        for (var index = 0; (index < plan.Controllers.Length); ++index) {
            plan.Controllers[index].Connect(link: cable);
        }

        m_controllers = plan.Controllers;
        m_machines = plan.Machines;
        m_targets = plan.Targets;
    }

    private static LinkPlan BuildFreshPlan(AgbMachineInstance[] consoles) {
        var (controllers, machines) = ValidateConsoles(consoles: consoles);
        var targets = new long[machines.Length];

        for (var index = 0; (index < machines.Length); ++index) {
            targets[index] = machines[index].Cycles;
        }

        return new LinkPlan(Controllers: controllers, Machines: machines, Targets: targets);
    }
    private static LinkPlan BuildResumePlan(AgbLinkResumeToken resumeToken, AgbMachineInstance[] consoles) {
        ArgumentNullException.ThrowIfNull(argument: resumeToken);

        var (controllers, machines) = ValidateConsoles(consoles: consoles);
        var credits = resumeToken.Credits;
        var identities = resumeToken.Identities;

        if (credits.Length != machines.Length) {
            throw new ArgumentException(message: $"the resume token carries {credits.Length} credits; the session joins {machines.Length} consoles.", paramName: nameof(resumeToken));
        }

        var targets = new long[machines.Length];

        for (var index = 0; (index < machines.Length); ++index) {
            if (credits[index] < 0) {
                throw new ArgumentException(message: $"the resume token's credit for console {index} is negative ({credits[index]}); a suspend credit is always non-negative.", paramName: nameof(resumeToken));
            }

            if (machines[index].Identity != identities[index]) {
                throw new ArgumentException(message: $"console {index}'s identity (format version / BIOS / ROM) does not match the resume token's binding for that position; consoles must reconnect in the same order they were suspended in.", paramName: nameof(consoles));
            }

            targets[index] = (machines[index].Cycles - credits[index]);
        }

        return new LinkPlan(Controllers: controllers, Machines: machines, Targets: targets);
    }

    // Shared shape/identity validation for both constructors: resolves each console's serial controller and machine,
    // rejects a null array, an out-of-range count, a null element, and a duplicate machine. Never connects anything —
    // callers wire the cable only once their own additional checks (resume-token credits/identities) also pass.
    private static (IAgbSerialController[] Controllers, AdvancedGamingBrickMachine[] Machines) ValidateConsoles(AgbMachineInstance[] consoles) {
        ArgumentNullException.ThrowIfNull(argument: consoles);

        if ((consoles.Length < 2) || (consoles.Length > AgbLinkCable.MaxPlayers)) {
            throw new ArgumentException(message: $"A link session joins 2 to {AgbLinkCable.MaxPlayers} consoles; got {consoles.Length}.", paramName: nameof(consoles));
        }

        var controllers = new IAgbSerialController[consoles.Length];
        var machines = new AdvancedGamingBrickMachine[consoles.Length];

        for (var index = 0; (index < consoles.Length); ++index) {
            var console = (consoles[index] ?? throw new ArgumentException(message: $"Console {index} is null.", paramName: nameof(consoles)));

            controllers[index] = console.GetRequiredService<IAgbSerialController>();
            machines[index] = console.Machine;

            for (var earlier = 0; (earlier < index); ++earlier) {
                if (ReferenceEquals(objA: machines[earlier], objB: machines[index])) {
                    throw new ArgumentException(message: $"Consoles {earlier} and {index} are the same machine; a console occupies exactly one chain position.", paramName: nameof(consoles));
                }
            }
        }

        return (controllers, machines);
    }

    /// <summary>Gets the number of consoles in the session (2–4; the parent is index 0).</summary>
    public int PlayerCount => m_machines.Length;

    /// <summary>Advances ALL machines forward by a shared budget of master-clock cycles, interleaved
    /// deterministically — the seam a host drives in place of the machines' own
    /// <see cref="AdvancedGamingBrickMachine.RunCycles"/> while they are linked.</summary>
    /// <param name="cycles">The number of master-clock cycles to advance each machine this call.</param>
    /// <exception cref="ObjectDisposedException">The session has been disposed.</exception>
    public void Run(long cycles) {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);

        for (var index = 0; (index < m_targets.Length); ++index) {
            m_targets[index] += cycles;
        }

        while (true) {
            // Step whichever machine is furthest behind its target so no machine ever observes another more than one
            // instruction stale; ties go to the lowest index — a fixed, state-free rule, so the interleave (and
            // therefore every word exchanged) replays identically for identical inputs.
            var furthest = -1;
            var furthestRemaining = 0L;

            for (var index = 0; (index < m_machines.Length); ++index) {
                var remaining = (m_targets[index] - m_machines[index].Cycles);

                if (remaining > furthestRemaining) {
                    furthest = index;
                    furthestRemaining = remaining;
                }
            }

            if (furthest < 0) {
                return;
            }

            m_machines[furthest].Step();
        }
    }

    /// <summary>Severs the cable and returns the credit token a later credit-preserving reconnect needs. Each
    /// console's credit is its instruction overshoot at this instant — the master-clock cycles it has already run
    /// past its cumulative link target (always non-negative: a completed <see cref="Run"/> leaves each console at or
    /// past its target). After suspend the session is disposed like <see cref="Dispose"/>; every console then steps
    /// independently and may be snapshotted, restored into fresh machines, and reconnected through the resume
    /// constructor with this token, in the same order.</summary>
    /// <returns>The token carrying every console's overshoot credit and identity, in session order.</returns>
    /// <exception cref="ObjectDisposedException">The session has already been disposed.</exception>
    /// <exception cref="InvalidOperationException">A transfer is armed or in flight (SIOCNT's start/busy bit set) on
    /// one or more consoles. This is the documented, enforced contract: suspend only at a transfer-idle instant,
    /// never mid-transfer — the session is left live and untouched so the caller can drive it to an idle boundary
    /// and retry.</exception>
    public AgbLinkResumeToken Suspend() {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);

        for (var index = 0; (index < m_controllers.Length); ++index) {
            if (m_controllers[index].IsTransferActive) {
                throw new InvalidOperationException(message: $"console {index} has a transfer armed or in flight (SIOCNT start/busy bit set); suspend only at a transfer-idle instant, never mid-transfer.");
            }
        }

        var credits = new long[m_targets.Length];
        var identities = new AgbMachineIdentity[m_targets.Length];

        for (var index = 0; (index < m_targets.Length); ++index) {
            credits[index] = (m_machines[index].Cycles - m_targets[index]);
            identities[index] = m_machines[index].Identity;
        }

        var token = new AgbLinkResumeToken(credits: credits, identities: identities);

        Dispose();

        return token;
    }
    /// <summary>Severs the cable: every serial controller reverts to the lone-console <see cref="NullAgbLink"/> and
    /// the machines step independently again. The machines themselves are untouched (they are owned by the caller,
    /// not the session).</summary>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;

        foreach (var controller in m_controllers) {
            controller.Connect(link: NullAgbLink.Instance);
        }
    }

    // The fully-validated wiring recipe a private constructor executes without any further checks: which controllers
    // to connect, the machines they belong to, and the pacing target each starts at.
    private readonly record struct LinkPlan(
        IAgbSerialController[] Controllers,
        AdvancedGamingBrickMachine[] Machines,
        long[] Targets
    );
}
