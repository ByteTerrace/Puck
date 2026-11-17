namespace Puck.Abstractions.Gpu;

/// <summary>A device generation's monotonic queue completion timeline. A checkpoint flushes deferred submissions
/// before signaling; it never waits on the CPU and never treats a failed signal as completion. All calls run on the
/// submission thread. The device owns this object and disposes it before destroying the native device.</summary>
public abstract class GpuQueueTimeline : IDisposable {
    private ulong m_next = 1;
    private bool m_disposed;

    /// <summary>Signals a fresh value after all work submitted so far, including an open batch. Pending commands
    /// not yet handed to the submitter are not covered; their owner must keep their resources until submission.</summary>
    /// <returns>The value successfully queued. Zero and the device-loss sentinel are never returned.</returns>
    public ulong Checkpoint() {
        ObjectDisposedException.ThrowIf(m_disposed, this);
        if (m_next == ulong.MaxValue) { throw new InvalidOperationException("The device timeline is exhausted."); }
        // Burn a value even when the native call fails with an unknown outcome; it can never certify a later use.
        var value = m_next++;
        Signal(value);
        return value;
    }

    /// <summary>Gets the native completed prefix. Device loss throws; it is never reported as a completed prefix.</summary>
    public ulong Completed {
        get {
            ObjectDisposedException.ThrowIf(m_disposed, this);
            return ReadCompleted();
        }
    }

    /// <summary>Flushes the queue's deferred work and queues the signal.</summary>
    /// <param name="value">The monotonically increasing value to signal.</param>
    protected abstract void Signal(ulong value);
    /// <summary>Reads native completion without waiting.</summary>
    /// <returns>The completed prefix.</returns>
    protected abstract ulong ReadCompleted();
    /// <summary>Destroys the native timeline after device drain or loss.</summary>
    protected abstract void DisposeNative();

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) { return; }
        m_disposed = true;
        DisposeNative();
        GC.SuppressFinalize(this);
    }
}
