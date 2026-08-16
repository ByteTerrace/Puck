using System.Collections.Concurrent;

namespace Puck.Commands;

/// <summary>A queue target for one principal-bound terminal text session.</summary>
public interface ITextCommandSink {
    /// <summary>Queues one command line for the host's next command-pump drain.</summary>
    /// <param name="line">The line to queue.</param>
    void Enqueue(string line);
}
/// <summary>
/// One host-issued text ingress, permanently bound to an acting principal and logical slot. A caller can submit text
/// through the session but cannot change the identity the host stamped on it.
/// </summary>
public sealed class TextCommandSession : ITextCommandSink {
    private readonly TextSubmissionBarrier m_barrier = new();
    private readonly ConcurrentQueue<string> m_pending = new();

    private readonly Action<string, CommandResult>? m_onResult;
    private readonly TextCommandSource m_source;

    internal TextCommandSession(
        TextCommandSource source,
        CommandPrincipal principal,
        int slot,
        CommandInjectionSink? simulationSink,
        Action<string, CommandResult>? onResult
    ) {
        m_onResult = onResult;
        m_source = source;
        Principal = principal;
        SimulationSink = simulationSink;
        Slot = slot;
    }

    internal TextSubmissionBarrier Barrier => m_barrier;
    internal bool HasPendingSimulationSubmission => m_barrier.HasPending;
    internal CommandInjectionSink? SimulationSink { get; }

    /// <summary>Gets the identity this ingress stamps on every submitted command.</summary>
    public CommandPrincipal Principal { get; }
    /// <summary>Gets the logical player slot this ingress targets.</summary>
    public int Slot { get; }

    internal void EnqueuePending(string line) => m_pending.Enqueue(item: line);
    internal void PublishResult(string line, CommandResult result) => m_onResult?.Invoke(
        line,
        result
    );
    internal bool TryDequeuePending(out string? line) => m_pending.TryDequeue(result: out line);
    internal bool TryPeekPending(out string? line) => m_pending.TryPeek(result: out line);

    /// <inheritdoc/>
    public void Enqueue(string line) {
        ArgumentNullException.ThrowIfNull(line);

        m_source.EnqueueSession(
            line: line,
            session: this
        );
    }
}

// Process-local coordination only: the reference rides a live snapshot entry so applying that exact submission
// releases only its originating session's read-after-write barrier.
internal sealed class TextSubmissionBarrier {
    private int m_pending;

    public bool HasPending => (m_pending != 0);

    public void Begin() => m_pending++;
    public void Complete() {
        if (m_pending != 0) {
            m_pending--;
        }
    }
}
