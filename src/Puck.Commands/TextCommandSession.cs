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
public sealed class TextCommandSession : ITextCommandSink, IDisposable {
    private readonly TextSubmissionBarrier m_barrier = new();
    private readonly ConcurrentQueue<TextSessionWork> m_pending = new();
    private readonly Lock m_enqueueGate = new();
    private Func<bool>? m_wait;
    private bool m_disposed;

    private readonly Action<string, CommandResult>? m_onResult;
    private readonly TextCommandSource m_source;

    internal TextCommandSession(
        TextCommandSource source,
        CommandPrincipal principal,
        int slot,
        CommandInjectionSink? simulationSink,
        Action<string, CommandResult>? onResult,
        Func<bool>? hold = null,
        Func<IDisposable>? scope = null
    ) {
        m_onResult = onResult;
        m_source = source;
        Hold = hold;
        Principal = principal;
        Scope = scope;
        SimulationSink = simulationSink;
        Slot = slot;
    }

    internal TextSubmissionBarrier Barrier => m_barrier;
    internal bool HasPendingSimulationSubmission => m_barrier.HasPending;
    // This session's own hold predicate, or null for a session nothing suspends on its own (the ordinary case; the
    // source-wide HoldGate is reserved for host-wide holds). While it returns true, Collect rotates this session to the
    // tail exactly like a read-after-write-blocked one, leaving every other session's drain unaffected.
    internal Func<bool>? Hold { get; }
    // Entered around this session's own dispatch of an Immediate line (Collect's synchronous call into the
    // registry) or host operation and disposed once the result is computed — a provider-neutral seam a host uses to make an ambient
    // label (which row a hosted session belongs to) available to whatever the handler calls synchronously, without
    // this project knowing what the label is for. Null for a session nothing ambient-labels (the ordinary case).
    internal Func<IDisposable>? Scope { get; }
    internal CommandInjectionSink? SimulationSink { get; }

    /// <summary>Gets the identity this ingress stamps on every submitted command.</summary>
    public CommandPrincipal Principal { get; }
    /// <summary>Gets the logical player slot this ingress targets.</summary>
    public int Slot { get; }

    internal void EnqueuePending(TextSessionWork work) {
        lock (m_enqueueGate) {
            if (m_disposed) {
                work.Refuse(new ObjectDisposedException(nameof(TextCommandSession)));
                throw new ObjectDisposedException(nameof(TextCommandSession));
            }

            m_pending.Enqueue(item: work);
        }
    }
    internal void PublishResult(string line, CommandResult result) => m_onResult?.Invoke(
        line,
        result
    );
    internal bool TryDequeuePending(out TextSessionWork? work) => m_pending.TryDequeue(result: out work);
    internal bool TryPeekPending(out TextSessionWork? work) => m_pending.TryPeek(result: out work);

    internal bool IsHolding() {
        if (Hold?.Invoke() ?? false) {
            return true;
        }

        if (m_wait?.Invoke() ?? false) {
            return true;
        }

        m_wait = null;
        return false;
    }

    /// <summary>Holds subsequent work in this session while the predicate returns true. Call only from the host
    /// pump, normally through an immediate handler's <see cref="CommandContext.TextSession"/>.</summary>
    /// <param name="hold">A short pump-thread predicate. A new hold replaces the previous session wait.</param>
    public void HoldWhile(Func<bool> hold) {
        ArgumentNullException.ThrowIfNull(argument: hold);
        m_wait = hold;
    }

    /// <summary>Queues a short host operation behind this session's preceding commands, mutation barriers and
    /// waits. The delegate runs on the command pump, never on the calling worker. It must not block on I/O or
    /// wait for the pump. Task continuations run asynchronously.</summary>
    /// <typeparam name="TResult">The operation's result type.</typeparam>
    /// <param name="operation">The short host operation, such as arming a render capture.</param>
    /// <param name="cancellationToken">Cancels queued work. Cancellation after execution starts cannot undo it.</param>
    /// <returns>The operation's result, exception, or queued cancellation.</returns>
    /// <exception cref="ObjectDisposedException">The session has closed.</exception>
    public ValueTask<TResult> InvokeAsync<TResult>(Func<TResult> operation, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(argument: operation);
        var work = new TextSessionOperation<TResult>(operation, cancellationToken);
        try {
            m_source.EnqueueSession(session: this, work: work);
        } catch (ObjectDisposedException) {
            // EnqueuePending already refused this operation; return its faulted task to the caller.
        }
        return new ValueTask<TResult>(work.Task);
    }

    /// <summary>Closes this ingress and refuses work still in its queue. Work already taken by the pump and injected
    /// simulation commands retain their normal execution semantics. Safe to call from a worker; disposal does not
    /// stop the host.</summary>
    public void Dispose() {
        lock (m_enqueueGate) {
            m_disposed = true;
            while (m_pending.TryDequeue(out var work)) {
                work.Refuse(new ObjectDisposedException(nameof(TextCommandSession)));
            }
        }
    }

    /// <inheritdoc/>
    public void Enqueue(string line) {
        ArgumentNullException.ThrowIfNull(line);

        m_source.EnqueueSession(
            work: new TextSessionLine(line),
            session: this
        );
    }
}

// Process-local coordination only: the reference rides a live snapshot entry so applying that exact submission
// releases only its originating session's read-after-write barrier.
//
// The count is written with interlocked operations even though the registry's own contract puts every Begin and
// Complete on the frame thread. It costs one uncontended CAS on a path that already parses and dispatches a command
// line, and it removes the need to reason about the contract twice: the reference rides a snapshot entry and a
// snapshot is a value a host can hold, so a torn read here would be a stranded session — the failure this whole
// mechanism exists to prevent — rather than an off-by-one nobody notices.
internal sealed class TextSubmissionBarrier {
    private int m_pending;

    public bool HasPending => (Volatile.Read(location: ref m_pending) != 0);

    public void Begin() => Interlocked.Increment(location: ref m_pending);
    public void Complete() {
        // A clamped decrement: the count never goes below zero, not even transiently, so a concurrent HasPending can
        // never read a negative count as "still pending".
        var pending = Volatile.Read(location: ref m_pending);

        while (pending != 0) {
            var observed = Interlocked.CompareExchange(
                comparand: pending,
                location1: ref m_pending,
                value: (pending - 1)
            );

            if (observed == pending) {
                return;
            }

            pending = observed;
        }
    }
}
