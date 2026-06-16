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

        while (m_pending.TryDequeue(result: out var line)) {
            if (string.IsNullOrWhiteSpace(value: line)) {
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
