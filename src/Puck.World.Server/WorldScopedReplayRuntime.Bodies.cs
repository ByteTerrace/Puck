namespace Puck.World.Server;

public sealed partial class WorldScopedReplayRuntime {
    // Arena dataflow does not describe world effects. Collect their conservative body targets too,
    // through every nested arm and decision option. -1 means an unbounded live target: refuse rather
    // than rewind a shared latch whose other bindings belong to the rest of the world.
    private void CollectBodyWrites(CompiledRule rule, HashSet<int> into) {
        CollectBodyWrites(effects: rule.Effects, into: into);
        if (rule is CompiledWorldFactsRule { Decision: { } decision }) {
            CollectBodyWrites(effects: decision.OnNoChoice, into: into);
            foreach (var option in decision.Options) { CollectBodyWrites(effects: option.Effects, into: into); }
        }
    }
    private void CollectBodyWrites(IRuleEffect[] effects, HashSet<int> into) {
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
                    CollectBodyWrite(key: motion.Key, keyFrom: motion.KeyFrom, into: into);
                    if (motion.Action.Operation == Puck.Physics.Motion.BodyMotionOp.Generate) { into.Add(item: -1); }
                    break;
                case WorldBodyStateEffect state:
                    CollectBodyWrite(key: state.Key, keyFrom: state.KeyFrom, into: into);
                    break;
                case WorldIdentityFactEffect identity:
                    CollectBodyWrite(key: identity.Key, keyFrom: identity.KeyFrom, into: into);
                    break;
            }
            foreach (var arm in effect.Arms) { CollectBodyWrites(effects: arm, into: into); }
        }
    }
    private void CollectBodyWrite(CellKey key, CompiledCellRef? keyFrom, HashSet<int> into) {
        into.Add(item: (((keyFrom is null) && RuleReads.TryKeyIndex(keys: m_server.Arena.Keys, key: key, index: out var index) && (index >= 0) && (index <= int.MaxValue)) ? (int)index : -1));
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
