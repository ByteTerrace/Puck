using System.Collections.Concurrent;

namespace Puck.Commands;

/// <summary>
/// A passive <see cref="ICommandSource"/> fed with command lines that are run through a registry's text
/// path, making a piped or scripted stream a first-class input.
/// </summary>
/// <remarks>
/// Lines are pushed in with <see cref="Enqueue"/> by any producer — for example, a host service that
/// reads standard input. Every frame, <see cref="Collect"/> drains the queued lines on the calling
/// thread and submits each non-blank line, surfacing the line and its <see cref="CommandResult"/>
/// through the optional result callback supplied at construction. The queue is thread-safe, so a
/// background producer may enqueue while the frame thread collects.
/// </remarks>
public sealed class TextCommandSource : ICommandSource {
    private readonly Action<string, CommandResult>? m_onResult;
    private readonly ConcurrentQueue<string> m_pending = new();
    private readonly CommandRegistry m_registry;

    /// <summary>An optional per-frame HOLD gate the drain honors: while it returns <see langword="true"/>,
    /// <see cref="Collect"/> dequeues nothing (and a line whose handler turns the gate on stops the drain immediately),
    /// so a queued command stream resumes only once the gate lets go. This is the seam that lets a scripted-console
    /// verb (a <c>step &lt;n&gt;</c> / <c>settle</c>) DEFER the rest of the piped script by a number of produced frames
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
        m_onResult = onResult;
    }

    /// <summary>Queues a command line to be submitted on the next <see cref="Collect"/>.</summary>
    /// <param name="line">The command line to queue. Blank lines are skipped when collected.</param>
    /// <exception cref="ArgumentNullException"><paramref name="line"/> is <see langword="null"/>.</exception>
    public void Enqueue(string line) {
        ArgumentNullException.ThrowIfNull(line);

        m_pending.Enqueue(item: line);
    }

    /// <summary>Submits every line enqueued since the last call, in arrival order.</summary>
    /// <param name="sink">The sink for the current frame. Unused; lines run through the registry's text path.</param>
    /// <exception cref="ArgumentNullException"><paramref name="sink"/> is <see langword="null"/>.</exception>
    public void Collect(ICommandSink sink) {
        ArgumentNullException.ThrowIfNull(sink);

        // Honor the HOLD gate BEFORE draining and AGAIN after each submitted line: a line whose handler arms the gate
        // (a step/settle verb) stops the drain for this frame, and the remaining queued lines wait for the gate to
        // release on a later frame — the queue itself is FIFO, so their order is preserved across the pause.
        //
        // The deferred-mutation barrier holds ONLY Immediate-routed lines: a pending simulation submission means an
        // inline read-back would observe pre-mutation state, so it waits for the snapshot to apply. Further
        // Simulation-routed lines keep draining — they fold into the same pending snapshot in FIFO order, so a burst
        // of scripted mutations lands in one tick instead of one per frame.
        while (!(HoldGate?.Invoke() ?? false) && m_pending.TryPeek(result: out var line)) {
            // Blank lines and '#' COMMENT lines are skipped, so a piped driving SCRIPT can be self-documenting: an
            // agent pipes a commented list of verbs (a "# what this run proves" header, per-step notes) and only the
            // real verbs run. A comment is a line whose first non-whitespace character is '#'.
            var content = line.AsSpan().TrimStart();
            var isComment = (content.IsEmpty || (content[0] == '#'));

            if (!isComment && m_registry.HasPendingSimulationSubmission && !m_registry.RoutesToSimulation(line: line)) {
                break;
            }

            // Collect is the queue's only consumer, so the peeked line is the one dequeued.
            _ = m_pending.TryDequeue(result: out _);

            if (isComment) {
                continue;
            }

            var result = m_registry.Submit(line: line);

            m_onResult?.Invoke(
                arg1: line,
                arg2: result
            );
        }
    }
}
