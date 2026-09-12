namespace Puck.World.Server;

public sealed partial class WorldServer {
    private bool m_authorityRetiring;

    /// <summary>Whether this activation has permanently closed admission and simulation for retirement.
    /// A failed final save does not reopen it; only a new server activation can accept work again.</summary>
    public bool IsRetiring { get { lock (m_authorityGate) { return m_authorityRetiring; } } }

    /// <summary>Drains already accepted document edits and freezes this activation under the authority gate.
    /// Call on the host pump before capturing the final host and server checkpoint slices.</summary>
    /// <exception cref="InvalidOperationException">A dispatched external operation has not settled yet.</exception>
    /// <remarks>Buffered intents remain checkpoint data; retirement does not invent an extra simulation tick.
    /// Repeated calls are harmless. Storage failure leaves the same frozen state available for another save.</remarks>
    public void FreezeForRetirement() {
        lock (m_authorityGate) {
            if (m_authorityRetiring) { return; }
            if (m_externalOperationsInFlight != 0) {
                throw new InvalidOperationException("Authority retirement requires external operations to settle first.");
            }
            // No other thread can admit work during this drain. Accepted extension contributions still use
            // the ordinary submission door, so admission closes only after that existing queue has settled.
            m_mutationBudget.BeginTick();
            DrainOrdered();
            DrainRecordedExtensions();
            _ = DrainPendingOps(tick: m_lastCompletedTick);
            m_authorityRetiring = true;
        }
    }

    private void ThrowIfAuthorityRetiring() {
        if (m_authorityRetiring) {
            throw new InvalidOperationException("The authority activation is retiring; use its current host.");
        }
    }
}
