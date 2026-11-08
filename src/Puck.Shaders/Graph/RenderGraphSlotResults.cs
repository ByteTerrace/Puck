namespace Puck.Shaders;

/// <summary>A package recorder's results by frame slot, from the recording that captured one to the node's proof that the
/// submission carrying it completed. A result is captured with its recording (<see cref="Record"/>), goes in flight when
/// the node reports the slot submitted (<see cref="Submitted"/>), and is delivered only when the node reports that slot's
/// submission complete (<see cref="Completed"/>), which the node does after waiting the slot and never for a submission a
/// device loss interrupted. A capture that never reached a submission is discarded at the slot's next completion, since
/// the node completes a slot before recording into it again. No fence is read here, and a package never receives one
/// (<see cref="IRenderGraphPackageReadback"/>): a reusable slot fence reads signaled after an abandoned submission and
/// after a loss's teardown drain alike. Single-threaded, on the frame thread.</summary>
/// <typeparam name="T">The captured result: a readback's destination and request, a publication, a submission's
/// identities.</typeparam>
public sealed class RenderGraphSlotResults<T> {
    private readonly Entry[] m_recorded;
    private readonly Entry[] m_submitted;
    private readonly Action<T> m_complete;
    private readonly Action<T>? m_submit;
    private readonly Action<T>? m_discard;

    /// <summary>Initializes a new instance of the <see cref="RenderGraphSlotResults{T}"/> class.</summary>
    /// <param name="slots">The frames the recorder's graph keeps in flight; positive.</param>
    /// <param name="complete">Receives a result once its submission is proven complete.</param>
    /// <param name="submit">Receives a result as its recording is submitted, or null.</param>
    /// <param name="discard">Receives a capture that never reached a submission, or null.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slots"/> is not positive.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="complete"/> is null.</exception>
    public RenderGraphSlotResults(int slots, Action<T> complete, Action<T>? submit = null, Action<T>? discard = null) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(slots);
        ArgumentNullException.ThrowIfNull(complete);
        m_recorded = new Entry[slots];
        m_submitted = new Entry[slots];
        m_complete = complete;
        m_submit = submit;
        m_discard = discard;
    }

    /// <summary>Captures a result with the slot's recording, discarding any earlier capture of the slot that never reached
    /// a submission.</summary>
    /// <param name="slot">The frame slot recorded into.</param>
    /// <param name="result">The captured result.</param>
    public void Record(int slot, T result) {
        Withdraw(slot: slot);
        m_recorded[slot] = new Entry(Has: true, Value: result);
    }
    /// <summary>Discards the slot's capture that has not reached a submission, if any.</summary>
    /// <param name="slot">The frame slot.</param>
    public void Withdraw(int slot) {
        if (!m_recorded[slot].Has) { return; }
        var earlier = m_recorded[slot].Value;

        m_recorded[slot] = default;
        m_discard?.Invoke(earlier);
    }
    /// <summary>Reports the slot's recording submitted: its capture goes in flight. A slot with no capture since its last
    /// submission submits nothing.</summary>
    /// <param name="slot">The submitted frame slot.</param>
    public void Submitted(int slot) {
        if (!m_recorded[slot].Has) { return; }
        var recorded = m_recorded[slot].Value;

        m_recorded[slot] = default;
        // The node completes a slot before recording into it, so an earlier submission still held here was completed by
        // that same proof.
        Deliver(slot: slot);
        m_submitted[slot] = new Entry(Has: true, Value: recorded);
        m_submit?.Invoke(recorded);
    }
    /// <summary>Reports the slot's previous submission complete: its result is delivered and a capture that never reached
    /// a submission is discarded. Completing a slot again delivers nothing.</summary>
    /// <param name="slot">The frame slot whose previous submission completed.</param>
    public void Completed(int slot) {
        Withdraw(slot: slot);
        Deliver(slot: slot);
    }
    /// <summary>Gives up every slot as the recorder is released: captures are discarded and every result still in flight
    /// goes to <paramref name="unproven"/>, never to completion.</summary>
    /// <param name="unproven">Receives each in-flight result, or null to drop them.</param>
    public void Release(Action<T>? unproven = null) {
        for (var slot = 0; (slot < m_submitted.Length); slot++) {
            Withdraw(slot: slot);
            if (!m_submitted[slot].Has) { continue; }
            var submitted = m_submitted[slot].Value;

            m_submitted[slot] = default;
            unproven?.Invoke(submitted);
        }
    }

    private void Deliver(int slot) {
        if (!m_submitted[slot].Has) { return; }
        var submitted = m_submitted[slot].Value;

        m_submitted[slot] = default;
        m_complete(submitted);
    }

    private readonly record struct Entry(bool Has, T Value);
}
