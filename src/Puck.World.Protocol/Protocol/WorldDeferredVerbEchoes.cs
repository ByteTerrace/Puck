namespace Puck.World.Protocol;

/// <summary>The local console's pending-verb table for deferred mutation verdicts: a buffered-mutation verb registers
/// the correlation id its submission minted, and the composition root's <c>WorldServer.EchoTap</c> subscriber takes
/// the entry back when the tick-boundary verdict fires, printing a per-verb refusal line
/// (<c>[world.row.set: …]</c>) the submitting script can account beside the verb-agnostic
/// <c>[world.mutation rejected: …]</c> narration. An accepted verdict takes its entry silently — echo model 3's
/// no-applied-result-line contract is unchanged.</summary>
/// <remarks>Register and take both run on the tick thread (a console handler submits inline over loopback; the echo
/// tap fires from the drain), so the table carries no lock. Correlation <c>0</c> means "no local correlation" (a
/// codec refusal, a federated link) and never registers. Past <see cref="Capacity"/> pending entries the oldest is
/// evicted, so a verdict that never fires cannot grow the table.</remarks>
public sealed class WorldDeferredVerbEchoes {
    /// <summary>The pending-entry bound. Mutations drain at the next tick boundary, so the steady-state population is
    /// one stdin batch's worth; the bound only matters when a verdict never fires.</summary>
    public const int Capacity = 256;

    private readonly Queue<long> m_order = new();
    private readonly Dictionary<long, string> m_verbs = [];

    /// <summary>Registers the submitting verb for one minted correlation id; correlation <c>0</c> (no local
    /// correlation) is ignored.</summary>
    /// <param name="correlationId">The correlation id the submission's envelope minted.</param>
    /// <param name="verb">The submitting verb, exactly as its response line spells it (e.g. <c>world.row.set</c>).</param>
    public void Register(long correlationId, string verb) {
        if (correlationId == 0) {
            return;
        }

        if (m_verbs.TryAdd(
            key: correlationId,
            value: verb
        )) {
            m_order.Enqueue(item: correlationId);

            // Evict oldest-first past the bound; an id whose entry was already taken dequeues as a no-op.
            while (m_verbs.Count > Capacity) {
                _ = m_verbs.Remove(key: m_order.Dequeue());
            }
        }
    }
    /// <summary>Takes the registered verb for one correlation id, removing the entry.</summary>
    /// <param name="correlationId">The verdict's correlation id.</param>
    /// <param name="verb">The verb registered at submit, when one was.</param>
    /// <returns><see langword="true"/> when an entry existed.</returns>
    public bool TryTake(long correlationId, out string verb) {
        if (m_verbs.TryGetValue(
            key: correlationId,
            value: out verb!
        )) {
            _ = m_verbs.Remove(key: correlationId);

            return true;
        }

        verb = string.Empty;

        return false;
    }
}
