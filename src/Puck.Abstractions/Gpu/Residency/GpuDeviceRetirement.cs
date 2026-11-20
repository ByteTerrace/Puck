namespace Puck.Abstractions.Gpu;

/// <summary>
/// The device's retirement of objects their owners let go of while queue work may still read them: a residency released
/// with views of it still in flight, a dropped portal's or routed view's tables. An owner hands an object over from any
/// thread (<see cref="Retire"/>) and never waits; whatever submits each frame polls the retirement after its batch
/// (<see cref="Poll"/>), which queues one timeline checkpoint behind everything submitted so far and disposes each object
/// once its checkpoint has completed; the device's owner releases everything at a device loss and at teardown
/// (<see cref="Drain"/>), before the device is recreated or destroyed. <see cref="GpuDeviceServices.Retirement"/> holds the
/// one instance of a device, which survives a device recreated in place.
/// <para>
/// The invariant is that an object is disposed only after a checkpoint queued after its retirement has completed, or
/// after a drain. One door enforces it: objects arrive only through <see cref="Retire"/>, and only a poll's checkpoint or
/// a drain disposes them. A checkpoint covers work handed to the submitter, deferred batches included, but not command
/// buffers recorded and not yet submitted, so an owner retires an object only once nothing will record against it again,
/// and every poller polls between its recordings and the next.
/// </para>
/// </summary>
public sealed class GpuDeviceRetirement {
    private readonly Lock m_gate = new();
    private readonly List<IDisposable> m_arrived = [];
    private readonly List<IDisposable> m_taken = [];
    private readonly GpuRetirementQueue<IDisposable> m_queue = new();
    private readonly Action<IDisposable> m_release;
    private List<Exception>? m_failures;

    /// <summary>Initializes a new instance of the <see cref="GpuDeviceRetirement"/> class.</summary>
    public GpuDeviceRetirement() => m_release = Release;

    /// <summary>Gets the number of objects retired and not yet disposed.</summary>
    public int Count {
        get {
            lock (m_gate) {
                return (m_arrived.Count + m_queue.Count);
            }
        }
    }

    /// <summary>Hands over an object no owner uses any more and that submitted queue work may still read. The retirement
    /// disposes it once a checkpoint queued after this call has completed, or at the next <see cref="Drain"/>. Any
    /// thread.</summary>
    /// <param name="item">The object; the retirement owns it from now on.</param>
    /// <exception cref="ArgumentNullException"><paramref name="item"/> is <see langword="null"/>.</exception>
    public void Retire(IDisposable item) {
        ArgumentNullException.ThrowIfNull(argument: item);

        lock (m_gate) {
            m_arrived.Add(item: item);
        }
    }
    /// <summary>Queues a checkpoint behind every object retired since the last poll and disposes those whose checkpoint
    /// has completed. Whatever submits each frame calls it on the submission thread once the frame's submissions are
    /// handed to the submitter. A retirement holding nothing reads no completion. Every disposal is attempted; the
    /// failures are thrown together after the last.</summary>
    /// <param name="timeline">The device generation's timeline (<see cref="IGpuQueueSubmitter.Timeline"/>).</param>
    /// <exception cref="ArgumentNullException"><paramref name="timeline"/> is <see langword="null"/>.</exception>
    /// <exception cref="AggregateException">A disposal threw.</exception>
    public void Poll(GpuQueueTimeline timeline) {
        ArgumentNullException.ThrowIfNull(argument: timeline);

        if (!TakeArrived() && (m_queue.Count == 0)) {
            return;
        }

        foreach (var item in m_taken) {
            m_queue.Hold(item: item);
        }

        m_taken.Clear();
        m_queue.Poll(release: m_release, timeline: timeline);
        ThrowFailures();
    }
    /// <summary>Disposes every retired object at once. The device's owner calls it on the submission thread after a
    /// device loss, before the device is recreated, and at teardown, once the device has finished every submission and
    /// before it is destroyed. Every disposal is attempted; the failures are thrown together after the last.</summary>
    /// <exception cref="AggregateException">A disposal threw.</exception>
    public void Drain() {
        _ = TakeArrived();

        foreach (var item in m_taken) {
            Release(item: item);
        }

        m_taken.Clear();
        m_queue.Release(completed: ulong.MaxValue, release: m_release);
        ThrowFailures();
    }

    // Moves what arrived since the last poll or drain to the submission thread's list.
    private bool TakeArrived() {
        lock (m_gate) {
            if (m_arrived.Count == 0) {
                return false;
            }

            m_taken.AddRange(collection: m_arrived);
            m_arrived.Clear();

            return true;
        }
    }
    private void Release(IDisposable item) {
        try {
            item.Dispose();
        } catch (Exception error) {
            (m_failures ??= []).Add(item: error);
        }
    }
    private void ThrowFailures() {
        if (m_failures is not { } failures) {
            return;
        }

        m_failures = null;

        throw new AggregateException(innerExceptions: failures, message: "Disposing a retired device object failed.");
    }
}
