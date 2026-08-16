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
/// <para><see cref="Enqueue"/> uses the administrative <see cref="CommandPrincipal.Console"/> session. A host can
/// mint a seat-bound ingress with <see cref="CreateSeatSession"/>; callers can submit through it but cannot alter its
/// fixed principal or slot.</para>
/// </remarks>
public sealed class TextCommandSource : ITextCommandSink {
    private readonly TextCommandSession m_administrativeSession;
    // One queue token per submitted line, while the line itself lives in its session's FIFO. Rotating a blocked
    // session therefore cannot move that session's oldest line behind a concurrently appended later line.
    private readonly ConcurrentQueue<TextCommandSession> m_pending = new();
    private readonly CommandRegistry m_registry;

    /// <summary>Gets or sets an optional per-frame hold gate the drain honors: while it returns <see langword="true"/>,
    /// <see cref="Collect"/> dequeues nothing (and a line whose handler turns the gate on stops the drain immediately),
    /// so a queued command stream resumes only once the gate lets go. This is the seam that lets a scripted-console
    /// verb (a <c>step &lt;n&gt;</c> / <c>settle</c>) defer the rest of the piped script by a number of produced frames
    /// or until a transition quiesces: the host sets a gate that counts produced frames, and the queued verbs after the
    /// gate wait on the frame boundary rather than all running the frame they arrive. <see langword="null"/> (the
    /// default) never holds, so an unwired run drains every line each frame exactly as before.</summary>
    public Func<bool>? HoldGate { get; set; }

    /// <summary>Initializes a new instance of the <see cref="TextCommandSource"/> class.</summary>
    /// <param name="registry">The registry whose text path each enqueued line is submitted to.</param>
    /// <param name="onResult">An optional callback invoked with each submitted line and its result.</param>
    /// <exception cref="ArgumentNullException"><paramref name="registry"/> is <see langword="null"/>.</exception>
    public TextCommandSource(CommandRegistry registry, Action<string, CommandResult>? onResult = null) {
        ArgumentNullException.ThrowIfNull(registry);

        m_registry = registry;
        m_administrativeSession = new TextCommandSession(
            source: this,
            principal: CommandPrincipal.Console,
            slot: 0,
            simulationSink: null,
            onResult: onResult
        );
    }

    internal void EnqueueSession(TextCommandSession session, string line) {
        session.EnqueuePending(line: line);
        m_pending.Enqueue(item: session);
    }

    /// <summary>Submits the lines present at entry in per-session arrival order. A session waiting for one of its
    /// simulation submissions rotates independently, so it cannot stall another seat's ready input.</summary>
    public void Collect() {
        // Honor the HOLD gate BEFORE draining and AGAIN after each submitted line: a line whose handler arms the gate
        // (a step/settle verb) stops the drain for this frame, and the remaining queued lines wait for the gate to
        // release on a later frame — the queue itself is FIFO, so their order is preserved across the pause.
        //
        // The deferred-mutation barrier holds ONLY Immediate-routed lines: a pending simulation submission means an
        // inline read-back would observe pre-mutation state, so it waits for the snapshot to apply. Further
        // Simulation-routed lines keep draining — they fold into the same pending snapshot in FIFO order, so a burst
        // of scripted mutations lands in one tick instead of one per frame.
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
                !session.TryPeekPending(line: out var line) ||
                (line is null)
            ) {
                continue;
            }

            // Blank lines and '#' COMMENT lines are skipped, so a piped driving SCRIPT can be self-documenting: an
            // agent pipes a commented list of verbs (a "# what this run proves" header, per-step notes) and only the
            // real verbs run. A comment is a line whose first non-whitespace character is '#'.
            var content = line.AsSpan().TrimStart();
            var isComment = (content.IsEmpty || (content[0] == '#'));

            if (blockedSessions?.Contains(item: session) ?? false) {
                m_pending.Enqueue(item: session);
                continue;
            }

            if (isComment) {
                _ = session.TryDequeuePending(line: out _);
                continue;
            }

            if (
                session.HasPendingSimulationSubmission &&
                !m_registry.RoutesToSimulation(line: line)
            ) {
                (blockedSessions ??= []).Add(item: session);
                m_pending.Enqueue(item: session);
                continue;
            }

            if (
                !session.TryDequeuePending(line: out line) ||
                (line is null)
            ) {
                continue;
            }

            var result = m_registry.SubmitSession(
                line: line,
                session: session
            );

            session.PublishResult(
                line: line,
                result: result
            );
        }
    }
    /// <summary>Creates a seat-authenticated text session over this source's shared queue and registry.</summary>
    /// <param name="router">The input router that mints the session's fixed simulation ingress.</param>
    /// <param name="slot">The local seat slot.</param>
    /// <param name="onResult">An optional callback for synchronous results produced by this session.</param>
    /// <returns>A text sink permanently stamped as <see cref="CommandPrincipal.Seat"/> for <paramref name="slot"/>.</returns>
    public TextCommandSession CreateSeatSession(InputRouter router, int slot, Action<string, CommandResult>? onResult = null) {
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

        return new TextCommandSession(
            source: this,
            principal: CommandPrincipal.Seat(slot: slot),
            slot: slot,
            simulationSink: router.CreateSeatTextSink(slot: slot),
            onResult: onResult
        );
    }
    /// <summary>Queues a command line to be submitted on the next <see cref="Collect"/>.</summary>
    /// <param name="line">The command line to queue. Blank lines are skipped when collected.</param>
    /// <exception cref="ArgumentNullException"><paramref name="line"/> is <see langword="null"/>.</exception>
    public void Enqueue(string line) {
        ArgumentNullException.ThrowIfNull(line);

        m_administrativeSession.Enqueue(line: line);
    }
}
