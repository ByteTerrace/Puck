namespace Puck.Abstractions.Gpu;

/// <summary>
/// The state every backend's <see cref="IGpuSubmissionFence"/> shares: a fence is drained, deferred in an open
/// <see cref="GpuSubmissionBatch"/>, answering from the fence its batch armed (aliased), or armed on the queue itself.
/// A backend supplies only its native signal: whether it has reached the armed value, a blocking wait for it, and the
/// reset after that wait. An aliased fence is signaled once its owner's native fence is, or once the owner has drained
/// (its generation moves on every successful drain or abandonment). Disposal drains outstanding work before releasing
/// the native fence; device loss remains visible to aliases after disposal. Single-threaded, like every pump-thread GPU object.
/// </summary>
public abstract class GpuBatchedSubmissionFence : IGpuSubmissionFence {
    private GpuSubmissionBatch? m_deferred;
    private GpuBatchedSubmissionFence? m_alias;
    private long m_aliasGeneration;
    private long m_generation;
    private bool m_armed;
    private bool m_disposed;
    private System.Runtime.ExceptionServices.ExceptionDispatchInfo? m_disposalFailure;

    /// <inheritdoc/>
    /// <remarks>A submission still deferred in an open batch is not signaled.</remarks>
    public bool IsSignaled {
        get {
            m_disposalFailure?.Throw();
            if (m_deferred is not null) { return false; }
            if (m_alias is { } alias) { return ((alias.m_generation != m_aliasGeneration) || alias.IsSignaled); }

            return (!m_armed || NativeSignaled());
        }
    }

    /// <summary>Refuses a fence with a submission outstanding: the caller must have drained it (<see cref="Wait"/>).</summary>
    /// <exception cref="InvalidOperationException">A submission is outstanding.</exception>
    public void RequireDrained() {
        ObjectDisposedException.ThrowIf(m_disposed, this);
        if (m_armed || (m_deferred is not null) || (m_alias is not null)) {
            throw new InvalidOperationException(message: "A submission is already outstanding on this fence; Wait before re-arming it.");
        }
    }
    /// <inheritdoc/>
    /// <remarks>A submission still deferred in an open batch splits it first (<see cref="GpuSubmissionBatch.Split"/>),
    /// keeping the batch open for the rest of its submissions.</remarks>
    public void Wait() {
        m_disposalFailure?.Throw();
        m_deferred?.Split(cause: GpuQueueCause.FenceWait);
        if (m_alias is { } alias) {
            // The owner moves its generation only once its submission retired, so an unmoved one is still the batch's.
            if (alias.m_generation == m_aliasGeneration) {
                alias.m_disposalFailure?.Throw();
                if (alias.m_armed) { alias.WaitNative(); }
            }
            m_alias = null;
            m_generation++;

            return;
        }
        if (!m_armed) { return; }

        WaitNative();
        DrainNative();
        m_armed = false;
        m_generation++;
    }
    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) { return; }
        try {
            Wait();
        } catch (DeviceLostException failure) {
            // Teardown may release a lost device's fence, but that is not successful completion. Keep the failure
            // at this generation so aliases can still report it without touching the released native object.
            m_disposalFailure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure);
        }
        m_deferred = null;
        m_alias = null;
        m_armed = false;
        m_disposed = true;
        DisposeNative();
        GC.SuppressFinalize(obj: this);
    }

    /// <summary>Marks the fence armed: its signal is on the queue behind its submission. The fence must be drained, or
    /// deferred in the batch now handing it over.</summary>
    /// <exception cref="InvalidOperationException">A submission is outstanding.</exception>
    protected void MarkArmed() {
        if (m_deferred is null) { RequireDrained(); }
        m_deferred = null;
        m_armed = true;
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

    internal void Defer(GpuSubmissionBatch batch) {
        RequireDrained();
        m_deferred = batch;
    }
    internal void Alias(GpuBatchedSubmissionFence owner) {
        m_deferred = null;
        m_alias = owner;
        m_aliasGeneration = owner.m_generation;
    }
    // A batch owner its backend never armed: its submission never reached the queue, so nothing is outstanding.
    internal void AbandonIfDeferred() {
        if (m_deferred is null) { return; }
        m_deferred = null;
        m_generation++;
    }
}
