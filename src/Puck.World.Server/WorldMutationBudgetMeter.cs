using Puck.World.Protocol;

namespace Puck.World.Server;

// The per-tick mutation-dispatch meter behind WorldServer.TryAdmitMutation's budget gate — ONE counter set for every
// untrusted ingress, so a peer and a mounted addon are metered by the same code against the same clock rather than
// each door keeping (and forgetting to keep) its own tally. Keyed by (principal, section) exactly as the grant row's
// own Budget is: a budget is authored per row, and a row names one section.
//
// Cleared once per fixed step, at the very top of WorldServer.Step — BEFORE WorldAddonRuntime.TickAddons' pre-flight
// and before DrainPendingOps applies what that pre-flight (and every peer submission buffered since the last step)
// enqueued, so both halves of one tick charge against the same allowance and the next tick starts fresh. A principal
// that never dispatches never gets an entry.
internal sealed class WorldMutationBudgetMeter {
    private readonly Dictionary<(WorldPrincipal Principal, WorldSection Section), int> m_charged = [];

    // Drop every count. O(capacity) over a dictionary whose live set is the number of (untrusted principal, section)
    // pairs that dispatched last tick — a handful, never a per-body or per-instance quantity.
    public void BeginTick() => m_charged.Clear();
    // Charge one dispatch against this row's per-tick allowance, or refuse when it is already spent. The charge lands
    // ONLY on success: a refused dispatch must not consume the allowance it was refused by, or a single over-budget
    // burst would silently extend the exhaustion past the tick that caused it.
    public bool TryCharge(WorldPrincipal principal, WorldSection section, ushort budget) {
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
