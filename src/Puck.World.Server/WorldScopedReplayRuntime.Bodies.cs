namespace Puck.World.Server;

public sealed partial class WorldScopedReplayRuntime {
    // A body target the rule's `forEach: $players` sweep binds to $each: a body a player holds, whichever that is.
    private const int PlayerBody = -2;

    // Arena dataflow does not describe world effects. Collect their conservative body targets too,
    // through every nested arm and decision option. -1 means an unbounded live target: refuse rather
    // than rewind a shared latch whose other bindings belong to the rest of the world. A write keyed by
    // $each in a sweep over the players is PlayerBody, which reaches the scope only through a body of the
    // scope a player can hold (ScopeHoldsPlayerBody).
    private void CollectBodyWrites(CompiledRule rule, HashSet<int> into) {
        var players = (rule.Iteration is WorldPlayerIteration);

        CollectBodyWrites(effects: rule.Effects, into: into, players: players);
        if (rule is CompiledWorldFactsRule { Decision: { } decision }) {
            CollectBodyWrites(effects: decision.OnNoChoice, into: into, players: players);
            foreach (var option in decision.Options) { CollectBodyWrites(effects: option.Effects, into: into, players: players); }
        }
    }
    // Whether a player can hold a body of the scope: a local seat's slot, or one an admitted visitor holds. A scope's
    // bodies are active through its run (its starting inventory is validated), so admission, which takes only a free
    // slot, cannot hand one of them to a visitor while the scope runs.
    private bool ScopeHoldsPlayerBody() => m_bodies.Any(predicate: body => ((body < m_server.Population.LocalSeatCount) || m_server.Population.IsAdmittedPeer(bodyIndex: body)));
    private void CollectBodyWrites(IRuleEffect[] effects, HashSet<int> into, bool players) {
        foreach (var effect in effects) {
            switch (effect) {
                case WorldPoseEffect pose:
                    into.Add(item: ((pose.KeyFrom is null) ? pose.Index : -1));
                    break;
                case WorldPoseCellEffect pose:
                    CollectBodyWrite(body: pose.Body, into: into);
                    break;
                case WorldRigidImpulseEffect impulse:
                    CollectBodyWrite(body: impulse.Target, into: into);
                    break;
                case WorldBodyMotionEffect motion:
                    CollectBodyWrite(key: motion.Key, keyFrom: motion.KeyFrom, into: into, players: players);
                    if (motion.Action.Operation == Puck.Physics.Motion.BodyMotionOp.Generate) { into.Add(item: -1); }
                    break;
                case WorldBodyStateEffect state:
                    CollectBodyWrite(key: state.Key, keyFrom: state.KeyFrom, into: into, players: players);
                    break;
                case WorldIdentityFactEffect identity:
                    CollectBodyWrite(key: identity.Key, keyFrom: identity.KeyFrom, into: into, players: players);
                    break;
            }
            foreach (var arm in effect.Arms) { CollectBodyWrites(effects: arm, into: into, players: players); }
        }
    }
    private void CollectBodyWrite(CellKey key, CompiledCellRef? keyFrom, HashSet<int> into, bool players) {
        into.Add(item: ((keyFrom is null) && m_server.Arena.Keys.TryCandidateIndex(key: key, index: out var index))
            ? index
            : ((players && (keyFrom is { IsEach: true })) ? PlayerBody : -1));
    }
    private void CollectBodyWrite(WorldBodyRef body, HashSet<int> into) {
        if (body.Kind == CompiledBodyRefKind.Literal) {
            into.Add(item: body.Index);
        } else if (body.Kind == CompiledBodyRefKind.Placement) {
            if (body.PlacementOrdinals is { } placements) {
                foreach (var placement in placements) { into.Add(item: m_server.Population.BodyForPlacementOrdinal(ordinal: placement)); }
            } else {
                into.Add(item: m_server.Population.BodyForPlacementOrdinal(ordinal: body.Index));
            }
        } else {
            into.Add(item: -1);
        }
    }
}
