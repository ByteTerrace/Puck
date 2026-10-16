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

    private readonly Action<string, CommandResult>? m_onResult;
    private readonly Action<string, CommandResult>? m_onSettled;
    private readonly TextCommandSource m_source;

    private volatile TextAnswerDeadline? m_answerDeadline;
    private volatile bool m_disposed;
    private SessionHold? m_heldBySubmit;
    private int m_resultCallbackFaults;
    private bool m_submitting;
    private bool m_tracked;
    private SessionHold? m_wait;

    internal TextCommandSession(
        TextCommandSource source,
        Principal principal,
        int slot,
        CommandInjectionSink? simulationSink,
        Action<string, CommandResult>? onResult,
        Func<bool>? hold = null,
        Func<IDisposable>? scope = null,
        Func<CommandMetadata, bool>? authorize = null,
        bool dueNextTick = false,
        Action<string, CommandResult>? onSettled = null
    ) {
        m_onResult = onResult;
        m_onSettled = onSettled;
        m_source = source;
        Hold = hold;
        Principal = principal;
        Scope = scope;
        SimulationSink = simulationSink;
        Slot = slot;
        Authorize = authorize;
        DueNextTick = dueNextTick;
    }

    internal Func<CommandMetadata, bool>? Authorize { get; }
    internal TextSubmissionBarrier Barrier => m_barrier;
    internal bool HasPendingSimulationSubmission => m_barrier.HasPending;
    internal bool SettlesResults => (m_onSettled is not null);
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

    /// <summary>Gets a value indicating whether a simulation-routed line from this ingress is due in the first tick
    /// that snapshots input after it was submitted, whatever the capture clock reads. A scripted ingress sets it:
    /// the capture clock is wall time, and a simulation running behind it would otherwise take the line up a
    /// varying number of ticks later.</summary>
    public bool DueNextTick { get; }
    /// <summary>Gets the identity this ingress stamps on every submitted command.</summary>
    public Principal Principal { get; }
    /// <summary>Gets the logical player slot this ingress targets.</summary>
    public int Slot { get; }
    /// <summary>Gets the number of result callbacks that threw. A reporting failure cannot cancel command execution.</summary>
    public int ResultCallbackFaults => Volatile.Read(location: ref m_resultCallbackFaults);

    internal void EnqueuePending(TextSessionWork work) {
        lock (m_enqueueGate) {
            if (m_disposed) {
                work.Refuse(exception: new ObjectDisposedException(objectName: nameof(TextCommandSession)));
                throw new ObjectDisposedException(objectName: nameof(TextCommandSession));
            }

            m_pending.Enqueue(item: work);
        }
    }
    // Reads this session's holds on the pump. The armed hold ends when its predicate releases it or when the answer
    // deadline it was armed under passes first; either way its release report runs once, and a line that armed it in a
    // settling session answers with that report.
    internal bool IsHolding() {
        if (Hold?.Invoke() ?? false) {
            return true;
        }

        if (m_wait is not { } wait) {
            return false;
        }

        if (m_disposed) {
            m_wait = null;

            return false;
        }

        var deadlinePassed = false;

        if (wait.Predicate()) {
            if (!(wait.Deadline?.HasPassed ?? false)) {
                return true;
            }

            deadlinePassed = true;
        }

        m_wait = null;
        Release(
            deadlinePassed: deadlinePassed,
            hold: wait
        );

        return false;
    }
    // Whether the pump must keep polling this session for a hold no queued line will read.
    internal bool PollHold() {
        var holding = IsHolding();

        m_tracked = holding;

        return holding;
    }

    private void Release(SessionHold hold, bool deadlinePassed) {
        CommandResult? report;

        try {
            report = hold.Released?.Invoke(arg: deadlinePassed);
        } catch (Exception error) {
            Interlocked.Increment(location: ref m_resultCallbackFaults);
            report = CommandResult.Error(output: $"[wire.hold: the hold's release report failed ({error.Message}); inspect state before any retry]");
        }

        if (hold.Line is not { } line) {
            return;
        }

        Publish(
            callback: m_onSettled,
            line: line,
            result: (report ?? (deadlinePassed
                ? CommandResult.Error(output: $"[wire.hold: '{line}' still held its session when this call's answer was due; the session no longer waits for it, so inspect state before any retry]")
                : hold.Armed))
        );
    }
    internal void BeginSubmit() {
        m_submitting = true;
        m_heldBySubmit = null;
    }
    // Ends a submit and reports its line's result, or nothing for a line queued into the simulation lane, which settles
    // when its tick applies. A line that armed a hold in a settling session and handed no settlement of its own answers
    // when the hold ends instead, so a caller awaiting the answer waits out the hold.
    internal void SettleSubmitted(string line, CommandResult? submitted) {
        var held = m_heldBySubmit;

        m_submitting = false;
        m_heldBySubmit = null;

        if (submitted is not { } result) {
            return;
        }

        if (
            SettlesResults &&
            (held is not null) &&
            ReferenceEquals(objA: held, objB: m_wait) &&
            (result.Settlement is null) &&
            !result.IsError
        ) {
            held.Line = line;
            held.Armed = result;

            return;
        }

        Settle(
            line: line,
            result: result
        );
    }

    // The line the registry queued into the simulation lane during the submit in flight, so the source can tell a
    // queued line, whose result settles when its tick applies, from one whose submit-time result is final.
    internal string? QueuedLine { get; set; }

    internal void PublishResult(string line, CommandResult result) => Publish(callback: m_onResult, line: line, result: result);

    private void Publish(Action<string, CommandResult>? callback, string line, CommandResult result) {
        try {
            callback?.Invoke(line, result);
        } catch (Exception) {
            Interlocked.Increment(location: ref m_resultCallbackFaults);
        }
    }

    // Reports a line's settled result: the handler's own, or the verdict of the work it started. While a verdict is
    // pending the barrier stays held, so this session's next line and its captures order behind it. A session that
    // settles nothing keeps the submit-time ordering it always had.
    internal void Settle(string line, CommandResult result) {
        if (m_onSettled is not { } onSettled) {
            return;
        }

        if (result.Settlement is not { } settlement) {
            Publish(callback: onSettled, line: line, result: result);

            return;
        }

        m_barrier.Begin();
        settlement.OnSettled(continuation: verdict => {
            try {
                Publish(callback: onSettled, line: line, result: verdict);
            } finally {
                m_barrier.Complete();
            }
        });
    }
    internal bool TryDequeuePending(out TextSessionWork? work) => m_pending.TryDequeue(result: out work);
    internal bool TryPeekPending(out TextSessionWork? work) => m_pending.TryPeek(result: out work);

    /// <summary>Closes this ingress and refuses work still in its queue. Work already taken by the pump and injected
    /// simulation commands retain their normal execution semantics. Safe to call from a worker; disposal does not
    /// stop the host.</summary>
    public void Dispose() {
        lock (m_enqueueGate) {
            m_disposed = true;
            while (m_pending.TryDequeue(result: out var work)) {
                work.Refuse(exception: new ObjectDisposedException(objectName: nameof(TextCommandSession)));
            }
        }
    }
    /// <inheritdoc/>
    public void Enqueue(string line) {
        ArgumentNullException.ThrowIfNull(line);

        m_source.EnqueueSession(
            work: new TextSessionLine(line: line),
            session: this
        );
    }
    /// <summary>Gets or sets the deadline by which the line this session submits next owes its caller an answer, or
    /// <see langword="null"/> when nothing bounds it, as for a piped script. A control attachment sets it for each
    /// call; a hold armed while it is set ends when it passes (<see cref="HoldWhile(Func{bool}, Func{bool, CommandResult?})"/>).</summary>
    public TextAnswerDeadline? AnswerDeadline {
        get => m_answerDeadline;
        set => m_answerDeadline = value;
    }

    /// <summary>Holds subsequent work in this session while the predicate returns true. Call only from the host
    /// pump, normally through an immediate handler's <see cref="CommandContext.TextSession"/>.</summary>
    /// <param name="hold">A short pump-thread predicate. A new hold replaces the previous session wait.</param>
    public void HoldWhile(Func<bool> hold) => HoldWhile(
        hold: hold,
        released: null
    );
    /// <summary>Holds subsequent work in this session while the predicate returns true, or until the
    /// <see cref="AnswerDeadline"/> current when the hold is armed passes, and reports the release. The pump reads the
    /// hold every drain, whether or not a line waits behind it, so <paramref name="released"/> runs as the hold ends.
    /// In a session that settles results, the line whose handler arms the hold answers only when the hold ends, with
    /// the report, or with the handler's own result when the report is <see langword="null"/>; a line ended by the
    /// deadline with no report answers with a refusal. Call only from the host pump, normally through an immediate
    /// handler's <see cref="CommandContext.TextSession"/>.</summary>
    /// <param name="hold">A short pump-thread predicate. A new hold replaces the previous session wait.</param>
    /// <param name="released">The release report, run once on the pump as the hold ends; its argument is
    /// <see langword="true"/> when the answer deadline ended the hold while the predicate still held. <see langword="null"/>
    /// reports nothing. A disposed session's hold ends without a report.</param>
    public void HoldWhile(Func<bool> hold, Func<bool, CommandResult?>? released) {
        ArgumentNullException.ThrowIfNull(argument: hold);
        var armed = new SessionHold(
            deadline: m_answerDeadline,
            predicate: hold,
            released: released
        );

        m_wait = armed;

        if (m_submitting) {
            m_heldBySubmit = armed;
        }

        if (!m_tracked) {
            m_tracked = true;
            m_source.TrackHold(session: this);
        }
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
        var work = new TextSessionOperation<TResult>(
            cancellationToken: cancellationToken,
            operation: operation
        );

        try {
            m_source.EnqueueSession(
                session: this,
                work: work
            );
        } catch (ObjectDisposedException) {
            // EnqueuePending already refused this operation; return its faulted task to the caller.
        }
        return new ValueTask<TResult>(task: work.Task);
    }
}

// One armed session hold: the line whose handler armed it and that line's own result, once the source has reported
// the line as held rather than settled.
internal sealed class SessionHold(Func<bool> predicate, Func<bool, CommandResult?>? released, TextAnswerDeadline? deadline) {
    public CommandResult Armed { get; set; }
    public TextAnswerDeadline? Deadline { get; } = deadline;
    public string? Line { get; set; }
    public Func<bool> Predicate { get; } = predicate;
    public Func<bool, CommandResult?>? Released { get; } = released;
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
