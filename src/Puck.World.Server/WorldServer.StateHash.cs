using Puck.Maths;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    // Hashes the runtime-only state feature lanes that are not represented in Definition.State. The owning server
    // supplies this seam because the edge latches intentionally remain private implementation details.
    internal void AppendStateFeatureHash(ref Fnv1aHash hash) {
        AppendDecisionHash(ref hash);
        m_ruleGateHeld.AppendStateHash(
            compiled: m_rules,
            hash: ref hash
        );
        m_interactionGateHeld.AppendStateHash(
            compiled: m_interactions,
            hash: ref hash
        );
        hash.Add(value: ((uint)m_population.Capacity));

        for (var index = 0; (index < m_population.Capacity); index++) {
            var body = m_population.EntryBody(index: index);

            hash.Add(value: ((byte)(body is null ? 0 : 1)));

            if (body is not null) {
                hash.Add(value: ((uint)index));
                body.AppendActionStateHash(hash: ref hash);
            }
        }
        m_population.AppendNavigationStateHash(hash: ref hash);
        m_population.AppendFlockStateHash(hash: ref hash);

    }
}
