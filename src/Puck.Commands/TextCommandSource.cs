using System.Collections.Concurrent;

namespace Puck.Commands;

/// <summary>
/// A passive queue of command lines that are run through a registry's text path, making a piped or scripted stream a
/// first-class input.
/// </summary>
/// <remarks>
/// Lines are pushed in with <see cref="Enqueue"/> by any producer — for example, a host service that
/// reads standard input. Every frame, <see cref="Collect"/> drains the queued lines on the calling
/// thread and submits each non-blank line, surfacing the line and its <see cref="CommandResult"/>
/// through its session's result callback. The queue is thread-safe, so background producers may enqueue while the
/// frame thread collects.
/// <para><see cref="Enqueue"/> uses the administrative <see cref="Principal.Console"/> session. A host can
/// mint a seat-bound ingress with <see cref="CreateSeatSession"/>; callers can submit through it but cannot alter its
/// fixed principal or slot.</para>
/// </remarks>
public sealed class TextCommandSource : ITextCommandSink {
    private readonly TextCommandSession m_administrativeSession;
    // One queue token per submitted work item, while the work itself lives in its session's FIFO. Rotating a blocked
    // session therefore cannot move that session's oldest line behind a concurrently appended later line.
    private readonly ConcurrentQueue<TextCommandSession> m_pending = new();
    private readonly CommandRegistry m_registry;

    // See HoldGate's remarks: volatile because a host may arm the gate from a thread other than the one that drains.
    private volatile Func<bool>? m_holdGate;

    internal void EnqueueSession(TextCommandSession session, TextSessionWork work) {
        session.EnqueuePending(work: work);
        m_pending.Enqueue(item: session);
    }

    /// <summary>Submits the lines present at entry in per-session arrival order. A session waiting for one of its
    /// simulation submissions rotates independently, so it cannot stall another seat's ready input.</summary>
    public void Collect() {
        // Honor the HOLD gate BEFORE draining and AGAIN after each submitted line: a line whose handler arms the gate
        // stops the drain for this frame, and the remaining queued lines wait for the gate to
        // release on a later frame — the queue itself is FIFO, so their order is preserved across the pause.
        //
        // The deferred-mutation barrier holds Immediate-routed lines and host operations: a pending simulation submission means an
        // inline read-back would observe pre-mutation state, so it waits for the snapshot to apply. Further
        // Simulation-routed lines in submit-time sessions keep draining — they fold into the same pending snapshot in FIFO order, so a burst
        // of scripted mutations lands in one tick instead of one per frame. Settling sessions hold every subsequent line.
        // Scan only the lines present at entry. A session whose read-after-write barrier is closed rotates to the
        // tail as one intact FIFO stream, allowing another seat's independent session to keep draining without
        // letting later lines from the blocked session overtake its read-back.
        var scanBudget = m_pending.Count;
        HashSet<TextCommandSession>? blockedSessions = null;

        while (
            (scanBudget-- > 0) &&
            !(HoldGate?.Invoke() ?? false) &&
            m_pending.TryDequeue(result: out var session)
        ) {
            if (
                !session.TryPeekPending(work: out var work) ||
                (work is null)
            ) {
                continue;
            }

            // Blank lines and '#' COMMENT lines are skipped, so a piped driving SCRIPT can be self-documenting: an
            // agent pipes a commented list of verbs (a "# what this run proves" header, per-step notes) and only the
            // real verbs run. A comment is a line whose first non-whitespace character is '#'.
            if (work.IsTerminal) {
                _ = session.TryDequeuePending(work: out _);
                work.Execute();
                continue;
            }

            var line = work.Line;
            var content = line.AsSpan().TrimStart();
            var isComment = ((line is not null) && (content.IsEmpty || (content[0] == '#')));

            if (blockedSessions?.Contains(item: session) ?? false) {
                m_pending.Enqueue(item: session);
                continue;
            }

            // A session's own hold — e.g. a world.wait deadline on the issuing session — rotates it to the tail exactly like a
            // read-after-write-blocked session below: nothing of THIS session's drains (comments included) while its
            // own hold stands, but every other session keeps draining independently.
            if (session.IsHolding()) {
                (blockedSessions ??= []).Add(item: session);
                m_pending.Enqueue(item: session);
                continue;
            }

            if (isComment) {
                _ = session.TryDequeuePending(work: out _);
                continue;
            }

            if (
                session.HasPendingSimulationSubmission &&
                (session.SettlesResults || (line is null) || !m_registry.RoutesToSimulation(line: line))
            ) {
                (blockedSessions ??= []).Add(item: session);
                m_pending.Enqueue(item: session);
                continue;
            }

            if (
                !session.TryDequeuePending(work: out work) ||
                (work is null)
            ) {
                continue;
            }

            if (work.Line is not { } commandLine) {
                work.Execute(scope: session.Scope);
                continue;
            }

            using (session.Scope?.Invoke()) {
                session.QueuedLine = null;

                CommandResult result;

                try {
                    result = m_registry.SubmitSession(line: commandLine, session: session);
                } catch (OperationCanceledException) {
                    session.Settle(line: commandLine, result: CommandResult.Error(output: "[wire.reject: the host cancelled this command; inspect state before any retry]"));
                    throw;
                }
                var queued = ReferenceEquals(
                    objA: session.QueuedLine,
                    objB: commandLine
                );

                session.QueuedLine = null;
                session.PublishResult(
                    line: commandLine,
                    result: result
                );

                // A queued line's result does not exist yet: it settles when its tick applies.
                if (!queued) {
                    session.Settle(
                        line: commandLine,
                        result: result
                    );
                }
            }
        }
    }

    /// <summary>Gets a value indicating whether the administrative session can make no further progress until a fixed
    /// step runs: its own hold stands (a tick wait), or its next queued line is held behind a Simulation submission
    /// only a step applies. Because the session is FIFO, every line queued ahead of that point has already run.</summary>
    /// <remarks>Read on the thread that calls <see cref="Collect"/>, after the drain.</remarks>
    public bool AdministrativeSessionAwaitsStep {
        get {
            var session = m_administrativeSession;

            if (session.IsHolding()) {
                return true;
            }

            if (
                !session.HasPendingSimulationSubmission ||
                !session.TryPeekPending(work: out var work) ||
                (work is null) ||
                work.IsTerminal
            ) {
                return false;
            }

            if (work.Line is not { } line) {
                return true;
            }

            var content = line.AsSpan().TrimStart();

            // A comment drains at once, and a further Simulation line joins the pending snapshot: neither waits.
            return (
                !content.IsEmpty &&
                (content[0] != '#') &&
                (session.SettlesResults || !m_registry.RoutesToSimulation(line: line))
            );
        }
    }

    /// <summary>Creates a seat-authenticated text session over this source's shared queue and registry.</summary>
    /// <param name="router">The input router that mints the session's fixed simulation ingress.</param>
    /// <param name="slot">The local seat slot.</param>
    /// <param name="onResult">An optional callback for synchronous results produced by this session.</param>
    /// <param name="dueNextTick">Whether a simulation-routed line is due in the next tick that snapshots input
    /// rather than at the capture clock's now (<see cref="TextCommandSession.DueNextTick"/>).</param>
    /// <returns>A text sink permanently stamped as <see cref="Principal.Seat"/> for <paramref name="slot"/>.</returns>
    public TextCommandSession CreateSeatSession(InputRouter router, int slot, Action<string, CommandResult>? onResult = null, bool dueNextTick = false) {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentOutOfRangeException.ThrowIfNegative(slot);

        if (!ReferenceEquals(
            objA: router.Registry,
            objB: m_registry
        )) {
            throw new ArgumentException(
                message: "The router and text source must use the same command registry.",
                paramName: nameof(router)
            );
        }

        return CreateSession(
            dueNextTick: dueNextTick,
            onResult: onResult,
            principal: Principal.Seat(slot: slot),
            simulationSink: router.CreateSeatTextSink(slot: slot),
            slot: slot
        );
    }
    /// <summary>Creates a text session over this source's shared queue and registry — the general form: a plain
    /// administrative ingress bound to a stamped identity, with no simulation lane and no fixed seat slot.
    /// <see cref="CreateSeatSession"/> is a caller of this method for the seat-bound shape.</summary>
    /// <param name="principal">The identity this session's lines are stamped with.</param>
    /// <param name="hold">An optional per-session hold predicate — while it returns <see langword="true"/>, this
    /// session rotates to the tail of the drain exactly like a read-after-write-blocked one, without affecting any
    /// other session. <see langword="null"/> (the default) never holds on its own.</param>
    /// <param name="onResult">An optional callback for synchronous results produced by this session.</param>
    /// <param name="slot">The logical slot this session's lines carry — 0 for an administrative session.</param>
    /// <param name="simulationSink">This session's fixed simulation ingress, or <see langword="null"/> for a session
    /// with no simulation lane.</param>
    /// <param name="scope">An optional ambient scope entered around this session's own dispatch of an
    /// <c>Immediate</c> line or a host operation and disposed once the result is computed — see <see cref="TextCommandSession.Scope"/>.
    /// <see langword="null"/> (the default) enters nothing.</param>
    /// <returns>A text sink permanently stamped with <paramref name="principal"/>.</returns>
    /// <param name="authorize">Optional command-metadata predicate checked before session dispatch; false refuses
    /// the command. Null adds no session-specific authorization predicate.</param>
    /// <param name="dueNextTick">Whether a simulation-routed line is due in the next tick that snapshots input
    /// rather than at the capture clock's now (<see cref="TextCommandSession.DueNextTick"/>).</param>
    /// <param name="onSettled">An optional callback invoked once per line with its settled result: the handler's own
    /// result, which for a simulation-routed line exists only when its tick applies, or the verdict of work the
    /// handler started (<see cref="CommandResult.Settlement"/>). A line refused at submission settles with that
    /// refusal. A session that settles results holds its next line until the line before it has settled; one that
    /// passes <see langword="null"/> keeps submit-time ordering.</param>
    public TextCommandSession CreateSession(Principal principal, Func<bool>? hold = null, Action<string, CommandResult>? onResult = null, int slot = 0, CommandInjectionSink? simulationSink = null, Func<IDisposable>? scope = null, Func<CommandMetadata, bool>? authorize = null, bool dueNextTick = false, Action<string, CommandResult>? onSettled = null) {
        return new TextCommandSession(
            authorize: authorize,
            dueNextTick: dueNextTick,
            hold: hold,
            onResult: onResult,
            onSettled: onSettled,
            principal: principal,
            scope: scope,
            simulationSink: simulationSink,
            slot: slot,
            source: this
        );
    }
    /// <summary>Describes registered commands selected by a trusted host policy, without exposing handlers.</summary>
    /// <param name="include">The disclosure filter, evaluated on the command pump.</param>
    /// <returns>Registered names and descriptions in ordinal order.</returns>
    public string DescribeCommands(Func<CommandMetadata, bool> include) => m_registry.BuildHelpText(include: include);
    /// <summary>Queues a command line to be submitted on the next <see cref="Collect"/>.</summary>
    /// <param name="line">The command line to queue. Blank lines are skipped when collected.</param>
    /// <exception cref="ArgumentNullException"><paramref name="line"/> is <see langword="null"/>.</exception>
    public void Enqueue(string line) {
        ArgumentNullException.ThrowIfNull(line);

        m_administrativeSession.Enqueue(line: line);
    }

    /// <summary>Gets or sets a source-wide hold gate. While it returns true, no session drains; a handler that
    /// arms it stops the current drain too. Use <see cref="TextCommandSession.HoldWhile"/> for a wait that belongs
    /// to only one session. Null leaves all ready sessions eligible to drain.</summary>
    /// <remarks>THREADING: unlike <see cref="Enqueue"/>, which any producer may call, this is read on the frame thread
    /// inside <see cref="Collect"/> and is expected to be set from there too — in practice by a handler the drain
    /// itself just ran. The backing field is <see langword="volatile"/> so a host that arms the gate from another
    /// thread is seen by the next drain rather than by whichever one the JIT decides to reload on; the gate's own
    /// delegate is invoked on the frame thread, so whatever it reads must be safe to read there.</remarks>
    public Func<bool>? HoldGate {
        get => m_holdGate;
        set => m_holdGate = value;
    }

    /// <summary>Initializes a new instance of the <see cref="TextCommandSource"/> class.</summary>
    /// <param name="registry">The registry whose text path each enqueued line is submitted to.</param>
    /// <param name="onResult">An optional callback invoked with each submitted line and its result.</param>
    /// <exception cref="ArgumentNullException"><paramref name="registry"/> is <see langword="null"/>.</exception>
    public TextCommandSource(CommandRegistry registry, Action<string, CommandResult>? onResult = null) {
        ArgumentNullException.ThrowIfNull(registry);

        m_registry = registry;
        // The administrative session is the piped-script ingress, so its simulation-routed lines are due in the next
        // step that snapshots input: a line released between two steps of a catch-up burst lands in the step right
        // after, not in whichever later step the wall-clock capture stamp first falls inside.
        m_administrativeSession = new TextCommandSession(
            dueNextTick: true,
            onResult: onResult,
            principal: Principal.Console,
            simulationSink: null,
            slot: 0,
            source: this
        );
    }
}
