using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>The per-tick mutation-dispatch meter behind <see cref="WorldGrants.TryAdmitMutation"/>'s budget gate —
/// one counter set for every untrusted ingress, so a peer and a mounted addon are metered by the same code against
/// the same clock rather than each door keeping (and forgetting to keep) its own tally. Keyed by
/// (principal, section) exactly as the grant row's own budget is: a budget is authored per row, and a row names one
/// section.</summary>
/// <remarks>Cleared once per fixed step, at the top of <see cref="WorldServer.Step"/> — before the addon pre-flight
/// and before the pending-op drain applies what that pre-flight (and every peer submission buffered since the last
/// step) enqueued, so both halves of one tick charge against the same allowance and the next tick starts fresh. A
/// principal that never dispatches never gets an entry.</remarks>
public sealed class WorldMutationBudgetMeter {
    private readonly Dictionary<(Principal Principal, WorldSection Section), int> m_charged = [];

    /// <summary>Drops every count.</summary>
    /// <remarks>O(capacity) over a dictionary whose live set is the number of (untrusted principal, section) pairs
    /// that dispatched last tick — a handful, never a per-body or per-instance quantity.</remarks>
    public void BeginTick() => m_charged.Clear();
    /// <summary>Charges one dispatch against this row's per-tick allowance, or refuses when it is already spent.</summary>
    /// <param name="principal">The dispatching principal.</param>
    /// <param name="section">The document section the dispatch targets.</param>
    /// <param name="budget">The row's authored per-tick allowance.</param>
    /// <returns>Whether the dispatch was charged.</returns>
    /// <remarks>The charge lands only on success: a refused dispatch must not consume the allowance it was refused
    /// by, or a single over-budget burst would silently extend the exhaustion past the tick that caused it.</remarks>
    public bool TryCharge(Principal principal, WorldSection section, ushort budget) {
        var key = (principal, section);
        ref var count = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(
            dictionary: m_charged,
            exists: out _,
            key: key
        );

        if (count >= budget) {
            return false;
        }

        count++;

        return true;
    }
}
