namespace Puck.Hosting;

/// <summary>
/// The in-process CPU frame-timing publish hub — the <see cref="PublishBuffer{T}"/> precedent hoisted for a
/// one-to-many fan-out. The launcher's window loop <see cref="Publish"/>es one <see cref="FrameTimingSample"/> per
/// iteration whenever GPU timing is armed; any number of observers read it. Two consumers today: the launcher's own
/// throttled <c>[frame-timing]</c> stderr digest (now one SUBSCRIBER of the hub instead of a private code path) and a
/// benchmark runner sampling the loop's CPU buckets. Registered as a DI singleton beside the present-pacing control.
/// <para>
/// Read paths: <see cref="Latest"/> is a lock-free snapshot of the most recent sample (a whole-value read of an
/// immutable record struct — no tearing, no locks), and <see cref="Version"/> is bumped per publish so a poller
/// detects a new sample without comparing contents. The <see cref="Published"/> event fires SYNCHRONOUSLY on the
/// publishing (render) thread, so handlers must be tiny — throttle, snapshot a few doubles, step a state machine —
/// never block or allocate on the hot path.
/// </para>
/// </summary>
public sealed class FrameTimingHub {
    private volatile Holder? m_latest;
    private ulong m_version;

    // A whole-reference swap per publish keeps the multi-field sample read tear-free without a lock (the PublishBuffer
    // idiom): a reader snapshots the reference and copies the immutable Frame, never observing a half-written struct.
    private sealed record Holder(FrameTimingSample Frame);

    /// <summary>Fires once per <see cref="Publish"/>, synchronously on the publishing (render) thread. Handlers must be
    /// tiny (see the type remarks).</summary>
    public event Action<FrameTimingSample>? Published;

    /// <summary>A monotonically increasing counter bumped on every <see cref="Publish"/> — lets a poller detect a new
    /// sample without comparing <see cref="Latest"/> field by field.</summary>
    public ulong Version => Volatile.Read(location: ref m_version);
    /// <summary>The most recently published sample (lock-free — a whole-reference read; a zeroed default until the
    /// first publish).</summary>
    public FrameTimingSample Latest => (m_latest?.Frame ?? default);

    /// <summary>Publishes this iteration's buckets (the launcher loop, once per iteration when timing is armed): stores
    /// the sample, bumps <see cref="Version"/>, then raises <see cref="Published"/> on the calling thread.</summary>
    /// <param name="sample">The frame's CPU-side buckets.</param>
    public void Publish(in FrameTimingSample sample) {
        m_latest = new Holder(Frame: sample);
        Volatile.Write(location: ref m_version, value: (Volatile.Read(location: ref m_version) + 1UL));
        Published?.Invoke(obj: sample);
    }
}
