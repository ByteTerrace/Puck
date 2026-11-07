using Puck.Maths;

namespace Puck.World.Server;

public sealed partial class WorldPopulation {
    // Tick scratch, shared by every projection and sweep retry for a body. Rebuilding the contact field does not
    // replenish it. Different body lanes own different slots; the tick owner resets it before dispatching them.
    private FixedQ4816[] m_adjacencyCorrectionSpent = [];

    private void BeginAdjacencyContacts() {
        if (m_adjacencies is null) { return; }
        if (m_adjacencyCorrectionSpent.Length != Capacity) {
            m_adjacencyCorrectionSpent = new FixedQ4816[Capacity];
        } else {
            Array.Clear(m_adjacencyCorrectionSpent);
        }
    }

    // The sole admission door for neighbour-body depenetration. The sum of admitted correction lengths per body
    // per tick cannot exceed its locally owned collider reach, nor the consulted overlap depth. Remote collider
    // sizes, poses, body counts and speed claims cannot enlarge that reach. No budget is refunded on a failed retry.
    internal FixedVector3 BoundAdjacencyCorrection(int entityIndex, FixedQ4816 overlapDepth, FixedVector3 correction) {
        if ((entityIndex < 0) || (entityIndex >= m_adjacencyCorrectionSpent.Length) ||
            (m_entries[entityIndex].Body is not { } body)) { return FixedVector3.Zero; }

        ref var spent = ref m_adjacencyCorrectionSpent[entityIndex];
        var ceiling = FixedQ4816.Max(FixedQ4816.Zero, FixedQ4816.Min(body.BroadphaseRadius, overlapDepth));
        var remaining = FixedQ4816.Max(FixedQ4816.Zero, ceiling - spent);
        var admitted = correction.ClampLength(maximum: remaining);
        if (admitted != correction) {
            WorldBodyWork.Process.Add(kind: WorldBodyWork.AdjacencyCorrectionClamps, amount: 1);
        }
        // Length rounds to nearest. Debit its ceiling, so repeated sub-raw diagonal corrections cannot exceed the
        // budget through accumulated rounding. IsWithin compares exact squares; ClampLength rounds toward zero.
        var length = admitted.Length;
        if (!admitted.IsWithin(radius: length)) { length += FixedQ4816.Epsilon; }
        spent += length;
        return admitted;
    }
}
