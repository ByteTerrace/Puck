using System.Runtime.ExceptionServices;

namespace Puck.Abstractions.Gpu;

/// <summary>
/// The state every backend's <see cref="IGpuSubmissionFence"/> shares: a fence is drained, deferred in an open
/// <see cref="GpuSubmissionBatch"/>, answering from the fence its batch's hand-over armed (aliased), armed on the queue itself,
/// abandoned, or faulted. A backend supplies only its native signal: whether it has reached the armed value, a blocking
/// wait for it, and the reset after that wait. An aliased fence is signaled once its owner's native fence is, or once the
/// owner has drained (its generation moves on every successful drain). A fence whose batch's hand-off failed before
/// anything reached the queue is abandoned: it reads as no proof at all, so <see cref="IsSignaled"/> and
/// <see cref="Wait"/> throw <see cref="GpuSubmissionAbandonedException"/>, and the wait that reports it drains the fence
/// for reuse. A fence whose submission reached the queue but whose signal could not be proven is faulted, and rethrows
/// that failure; so is a disposed fence whose device was lost. Single-threaded, like every pump-thread GPU object.
/// <para>A completion signal (<see cref="SignalOnCompletion"/>) follows the same states: asked of a deferred fence it
/// waits for the batch's hand-off, asked of an aliased one it is the owner's, and only an armed fence asks its backend
/// (<see cref="TrySignalNative"/>). A backend that cannot register the event hands the fence to the shared watcher
/// (<see cref="GpuFenceWatch"/>), which waits it in the driver (<see cref="WaitNativeFor"/>) until it completes. Every
/// state that ends without a native signal (abandoned, faulted, already retired) sets the event itself, so a waiter is
/// never left asleep on a submission that will not signal.</para>
/// </summary>
public abstract class GpuBatchedSubmissionFence : IGpuSubmissionFence {
    private GpuSubmissionBatch? m_deferred;
    private GpuBatchedSubmissionFence? m_alias;
    private long m_aliasGeneration;
    private long m_generation;
    private bool m_abandoned;
    private bool m_armed;
    private bool m_disposed;
    // Whether the shared watcher was handed the armed submission; it is withdrawn before the native fence is reset or
    // released.
    private bool m_watched;
    private ExceptionDispatchInfo? m_failure;
    // The completion signal asked for while the submission was still deferred, owed to the backend once it is armed.
    private EventWaitHandle? m_owedSignal;

    /// <inheritdoc/>
    /// <remarks>A submission still deferred in an open batch is not signaled.</remarks>
    /// <exception cref="GpuSubmissionAbandonedException">The submission never reached the queue.</exception>
    public bool IsSignaled {
        get {
            m_failure?.Throw();
            if (m_abandoned) { throw new GpuSubmissionAbandonedException(); }
            if (m_deferred is not null) { return false; }
            if (m_alias is { } alias) { return ((alias.m_generation != m_aliasGeneration) || alias.IsSignaled); }

            return (!m_armed || NativeSignaled());
        }
    }

    /// <summary>Refuses a fence with a submission outstanding, an abandoned one included: the caller must have drained it
    /// (<see cref="Wait"/>).</summary>
    /// <exception cref="InvalidOperationException">A submission is outstanding.</exception>
    public void RequireDrained() {
        ObjectDisposedException.ThrowIf(m_disposed, this);
        if (m_armed || m_abandoned || (m_deferred is not null) || (m_alias is not null)) {
            throw new InvalidOperationException(message: "A submission is already outstanding on this fence; Wait before re-arming it.");
        }
    }
    /// <inheritdoc/>
    /// <remarks>A submission still deferred in an open batch splits it first (<see cref="GpuSubmissionBatch.Split"/>),
    /// keeping the batch open for the rest of its submissions; a split that fails before this fence's submission reaches
    /// the queue reports it abandoned.</remarks>
    /// <exception cref="GpuSubmissionAbandonedException">The submission never reached the queue, so nothing proves it
    /// ran; the fence is drained.</exception>
    public void Wait() {
        m_failure?.Throw();
        try {
            m_deferred?.Split(cause: GpuQueueCause.FenceWait);
        } catch (Exception failure) {
            // Not a filter: a filter runs before the split's finally has marked this fence abandoned.
            if (m_abandoned) { ReportAbandoned(failure: failure); }

            throw;
        }
        if (m_abandoned) { ReportAbandoned(failure: null); }
        if (m_alias is { } alias) {
            // The owner moves its generation only once its submission retired, so an unmoved one is still the batch's.
            if (alias.m_generation == m_aliasGeneration) {
                alias.m_failure?.Throw();
                if (alias.m_armed) { alias.WaitNativeCounted(); }
            }
            m_alias = null;
            m_generation++;

            return;
        }
        if (!m_armed) { return; }

        WaitNativeCounted();
        Unwatch();
        DrainNative();
        m_armed = false;
        m_generation++;
    }
    /// <inheritdoc/>
    /// <remarks>Splits the batch (<see cref="GpuSubmissionBatch.Split"/>) under <see cref="GpuQueueCause.FenceWait"/>, as
    /// a wait on the deferred fence does, so the hand-off arms this fence or answers it from the hand-off's owner and settles
    /// a completion signal it owed. A split that fails before this fence's submission reaches the queue leaves it
    /// abandoned, which its next <see cref="IsSignaled"/> or <see cref="Wait"/> reports.</remarks>
    public void HandOff() {
        m_failure?.Throw();
        m_deferred?.Split(cause: GpuQueueCause.FenceWait);
    }
    /// <inheritdoc/>
    /// <remarks>A fence deferred in an open batch keeps the signal until the batch's hand-off arms it or answers it from
    /// the hand-off's owner.</remarks>
    public void SignalOnCompletion(EventWaitHandle signal) {
        ArgumentNullException.ThrowIfNull(signal);
        if ((m_failure is not null) || m_abandoned || m_disposed) {
            _ = signal.Set();
        } else if (m_deferred is not null) {
            m_owedSignal = signal;
        } else if (m_alias is { } alias) {
            if (alias.m_generation != m_aliasGeneration) { _ = signal.Set(); } else { alias.SignalOnCompletion(signal: signal); }
        } else if (!m_armed) {
            _ = signal.Set();
        } else {
            SignalArmed(signal: signal);
        }
    }
    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) { return; }
        try {
            Wait();
        } catch (GpuSubmissionAbandonedException) {
            // Nothing reached the queue, so nothing is outstanding.
        } catch (DeviceLostException failure) {
            // Teardown may release a lost device's fence, but that is not successful completion. Keep the failure
            // at this generation so aliases can still report it without touching the released native object.
            m_failure ??= ExceptionDispatchInfo.Capture(failure);
        }
        m_deferred = null;
        m_alias = null;
        m_armed = false;
        m_disposed = true;
        SettleOwedSignal();
        Unwatch();
        DisposeNative();
        GC.SuppressFinalize(obj: this);
    }

    /// <summary>Refuses a fence that cannot take the submission now reaching the queue: it must be drained, or deferred in
    /// the batch now handing it over. A backend that queues its signal in a call that can fail checks this first, so the
    /// failure leaves the fence as it was.</summary>
    /// <exception cref="InvalidOperationException">A submission is outstanding.</exception>
    protected void RequireArmable() {
        if (m_deferred is null) { RequireDrained(); }
    }
    /// <summary>Marks the fence armed: its signal is on the queue behind its submission. The fence must be drained, or
    /// deferred in the batch now handing it over.</summary>
    /// <exception cref="InvalidOperationException">A submission is outstanding.</exception>
    protected void MarkArmed() {
        RequireArmable();
        m_deferred = null;
        m_armed = true;
        if (m_owedSignal is { } signal) {
            m_owedSignal = null;
            SignalArmed(signal: signal);
        }
    }
    /// <summary>Queues the signal of a submission whose entries are already on the queue, and arms the fence only once
    /// the signal call returned. A refused signal cannot leave the fence reading drained over work that runs: the queue
    /// is drained behind the entries and the fence marked as having run (it and every fence answering from it are done),
    /// or, when that drain fails too, the fence is faulted, so every later <see cref="Wait"/> or
    /// <see cref="IsSignaled"/> on it or a fence answering from it rethrows the drain's failure. The signal's failure is
    /// rethrown either way.</summary>
    /// <typeparam name="TState">The state the signal call reads.</typeparam>
    /// <param name="state">The state handed to <paramref name="queueSignal"/>.</param>
    /// <param name="queueSignal">Queues the native signal behind the entries; throws when the backend refuses it.</param>
    /// <param name="drainQueue">Blocks until the queue has finished everything put on it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="queueSignal"/> or <paramref name="drainQueue"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A submission is outstanding.</exception>
    protected void ArmBehind<TState>(TState state, Action<TState> queueSignal, Action drainQueue) {
        ArgumentNullException.ThrowIfNull(queueSignal);
        ArgumentNullException.ThrowIfNull(drainQueue);
        RequireArmable();

        try {
            queueSignal(state);
        } catch {
            try {
                drainQueue();
                m_deferred = null;
                m_generation++;
            } catch (Exception drainFailure) {
                m_deferred = null;
                m_failure = ExceptionDispatchInfo.Capture(source: drainFailure);
            }
            SettleOwedSignal();

            throw;
        }

        MarkArmed();
    }
    /// <summary>Gets whether the native fence has reached the value its submission signals; a lost device throws
    /// <see cref="DeviceLostException"/>.</summary>
    protected abstract bool NativeSignaled();
    /// <summary>Blocks until the native fence reaches the value its submission signals, leaving it armed.</summary>
    protected abstract void WaitNative();
    /// <summary>Readies the native fence for its next submission, once <see cref="WaitNative"/> returned.</summary>
    protected abstract void DrainNative();
    /// <summary>Releases the native fence.</summary>
    protected abstract void DisposeNative();
    /// <summary>Asks the backend to set <paramref name="signal"/> once the native fence reaches the value its armed
    /// submission signals, or at once when it already has. Called only while the fence is armed, on the thread that
    /// submits; it must not block.</summary>
    /// <param name="signal">The event to set.</param>
    /// <returns>Whether the backend registered the event with the device; <see langword="false"/> when it has no such
    /// registration or the device refused it, and the shared watcher then waits the fence
    /// (<see cref="WaitNativeFor"/>) and sets the event.</returns>
    protected abstract bool TrySignalNative(EventWaitHandle signal);
    /// <summary>Blocks for at most <paramref name="slice"/> until the native fence reaches the value its armed submission
    /// signals. Called on a watcher thread while the fence is armed, concurrently with the submitting thread's reads and
    /// waits but never with <see cref="DrainNative"/> or <see cref="DisposeNative"/>, which the watcher is withdrawn
    /// from first; it must be safe under that concurrency, and a throw (a lost device) ends the watch as reached.</summary>
    /// <param name="slice">The longest the call may block.</param>
    /// <returns>Whether the fence reached its value; <see langword="false"/> when the slice passed first.</returns>
    protected internal abstract bool WaitNativeFor(TimeSpan slice);

    // Asks the backend for a completion event, or has the shared watcher wait the fence when the backend cannot.
    private void SignalArmed(EventWaitHandle signal) {
        if (!TrySignalNative(signal: signal)) {
            m_watched = true;
            GpuFenceWatch.Begin(fence: this, signal: signal);
        }
    }
    // Takes the fence back from the shared watcher before its native fence is reset or released.
    private void Unwatch() {
        if (m_watched) {
            m_watched = false;
            GpuFenceWatch.Withdraw(fence: this);
        }
    }

    // Drains an abandoned fence, so it can be armed again, and reports the abandonment to the caller that waited.
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private void ReportAbandoned(Exception? failure) {
        m_abandoned = false;
        m_generation++;

        throw new GpuSubmissionAbandonedException(innerException: failure);
    }
    // Every blocking wait goes through here, so the calling thread's blocked time (GpuFenceBlocking) covers them all.
    private void WaitNativeCounted() {
        var start = System.Diagnostics.Stopwatch.GetTimestamp();

        try {
            WaitNative();
        } finally {
            GpuFenceBlocking.Add(ticks: (System.Diagnostics.Stopwatch.GetTimestamp() - start));
        }
    }
    internal bool IsDeferred => (m_deferred is not null);
    internal void Defer(GpuSubmissionBatch batch) {
        RequireDrained();
        m_deferred = batch;
    }
    internal void Alias(GpuBatchedSubmissionFence owner) {
        m_deferred = null;
        m_alias = owner;
        m_aliasGeneration = owner.m_generation;
        if (m_owedSignal is { } signal) {
            m_owedSignal = null;
            owner.SignalOnCompletion(signal: signal);
        }
    }
    // Sets a signal owed to a submission that will now never ask its backend: abandoned, faulted, run by a drain, or
    // disposed. Whoever wakes reads the fence and finds which.
    private void SettleOwedSignal() {
        if (m_owedSignal is { } signal) {
            m_owedSignal = null;
            _ = signal.Set();
        }
    }
    /// <summary>Marks a submission that never reached the queue as abandoned: a batch's hand-off, or the backend's own
    /// submit, failed before anything was put on the queue. Every later <see cref="IsSignaled"/> reports it, and the next
    /// <see cref="Wait"/> reports it and drains the fence. The fence must be drained, or deferred in the batch now
    /// handing it over.</summary>
    /// <exception cref="InvalidOperationException">A submission is outstanding.</exception>
    protected internal void MarkAbandoned() {
        RequireArmable();
        m_deferred = null;
        m_abandoned = true;
        SettleOwedSignal();
    }
}
