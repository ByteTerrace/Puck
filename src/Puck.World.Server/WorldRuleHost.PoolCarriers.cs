namespace Puck.World.Server;

public sealed partial class WorldRuleHost {
    private StateInstanceHandle[] m_poolCarrierLeft = [];
    private StateInstanceHandle[] m_poolCarrierRight = [];
    private int[] m_poolCarrierLeftBody = [];
    private int[] m_poolCarrierRightBody = [];

    private void InteractionCarriers(string name, StatePoolDescriptor? pool, CompiledPoolBodyCarrier? carrier, List<int> into, ref StateInstanceHandle[] snapshots, ref int[] bodies) {
        if (pool is null) {
            Carriers(rowOrdinal: CarrierRowOrdinal(name: name), into: into);
            return;
        }
        if (carrier is null) {
            throw new InvalidOperationException(message: $"Compiled pool '{pool.Name}' has no carrier mapping.");
        }
        if (snapshots.Length < pool.Capacity) {
            Array.Resize(array: ref snapshots, newSize: pool.Capacity);
            Array.Resize(array: ref bodies, newSize: pool.Capacity);
        }
        into.Clear();
        var count = Host.Arena.CopyPoolSnapshot(poolOrdinal: pool.Ordinal, destination: snapshots);
        // Scatter backwards: ascending live slots cannot overwrite an unread compacted handle.
        for (var index = (count - 1); (index >= 0); index--) {
            var handle = snapshots[index];

            if (!Host.Arena.TryReadLiveRaw(fieldOrdinal: carrier.FieldOrdinal, handle: handle, raw: out var member, time: Time)) {
                continue;
            }
            if (((ulong)member) >= ((ulong)carrier.Bindings.Count)) {
                throw new InvalidOperationException(message: $"Pool '{pool.Name}' holds a carrier value outside its admitted enum.");
            }
            var binding = carrier.Bindings[((int)member)];
            var body = ((binding.Kind == CompiledBodyRefKind.Placement)
                ? Host.Population.BodyForPlacementOrdinal(ordinal: binding.Index)
                : binding.Index);

            if ((body < 0) || (body >= Host.Population.Capacity) || (Host.Body(index: body) is null)) {
                continue;
            }
            snapshots[handle.Slot] = handle;
            bodies[handle.Slot] = body;
            into.Add(item: handle.Slot);
        }
        into.Reverse();
    }
    private bool FireInteractionPair(CompiledWorldFactsRule rule, CompiledInteraction interaction, RuleLatch latch,
        Dictionary<LatchKey, bool> bindings, ulong stepTicks, int left, int right) {
        var leftHandle = ((interaction.LeftPool is null) ? default : m_poolCarrierLeft[left]);
        var rightHandle = ((interaction.RightPool is null) ? default : m_poolCarrierRight[right]);

        if (((interaction.LeftPool is not null) && !Host.Arena.TryResolve(handle: leftHandle, position: out _)) ||
            ((interaction.RightPool is not null) && !Host.Arena.TryResolve(handle: rightHandle, position: out _))) {
            return false;
        }
        m_boundLeft = ((interaction.LeftPool is null) ? left : m_poolCarrierLeftBody[left]);
        m_boundRight = ((interaction.RightPool is null) ? right : m_poolCarrierRightBody[right]);
        if (interaction.LeftBinding >= 0) {
            _ = m_evaluator.TrySetInstanceBinding(register: interaction.LeftBinding, handle: leftHandle);
        }
        if (interaction.RightBinding >= 0) {
            _ = m_evaluator.TrySetInstanceBinding(register: interaction.RightBinding, handle: rightHandle);
        }
        try {
            _ = m_evaluator.EvaluateOnce(rule: rule, latch: latch, bindings: bindings,
                binding: new LatchKey(Left: left, Right: right, LeftGeneration: leftHandle.Generation, RightGeneration: rightHandle.Generation),
                stepTicks: stepTicks, applied: out var applied);
            return applied;
        } finally {
            m_evaluator.ClearInstanceBinding(register: interaction.RightBinding);
            m_evaluator.ClearInstanceBinding(register: interaction.LeftBinding);
        }
    }
}
