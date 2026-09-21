namespace Puck.State.Rules;

public static partial class ArenaTransforms {
    private static bool TryPushRay(in ArenaTransformContext context, ArenaTransform.PushRay push, in ArenaTransformBinding binding, out bool moved, out EffectRefusal refusal) {
        moved = false;
        if (!binding.BindsInstance || (binding.Instance.PoolOrdinal != push.PoolOrdinal) || (((uint)push.PoolOrdinal) >= ((uint)context.Arena.Catalog.Pools.Count))) {
            return Refuse(code: TransformRefusal.PushRayAddressing, reason: "pushRay requires a live mover from its declared pool", refusal: out refusal);
        }

        var pool = context.Arena.Catalog.Pools[push.PoolOrdinal];

        if (pool.IsPair || (((uint)push.CellFieldOrdinal) >= ((uint)pool.Fields.Count)) || (((uint)push.ValueFieldOrdinal) >= ((uint)pool.Fields.Count)) || (((uint)push.Direction) >= ((uint)push.Topology.DirectionCount))) {
            return Refuse(code: TransformRefusal.PushRayAddressing, reason: "pushRay requires an ordinary pool, two scalar fields, and a topology direction", refusal: out refusal);
        }

        if (!context.Arena.TryReadLiveRaw(fieldOrdinal: push.CellFieldOrdinal, handle: binding.Instance, raw: out var originCell, time: context.Time) || (originCell < 0L) || (originCell >= push.Topology.CellCount)) {
            return Refuse(code: TransformRefusal.PushRayAddressing, reason: "pushRay's live origin lies outside its topology", refusal: out refusal);
        }
        var origin = ((int)originCell);

        using var visitedLease = context.Arena.Scratch.Rent<byte>(length: push.Topology.CellCount);
        var visitedCells = visitedLease.Span;

        visitedCells[origin] = 1;
        using var handlesLease = context.Arena.Scratch.Rent<StateInstanceHandle>(length: pool.Capacity);
        var handles = handlesLease.Span;
        var liveCount = context.Arena.CopyPoolSnapshot(poolOrdinal: pool.Ordinal, destination: handles);
        using var headsLease = context.Arena.Scratch.Rent<int>(length: push.Topology.CellCount);
        using var tailsLease = context.Arena.Scratch.Rent<int>(length: push.Topology.CellCount);
        using var nextLease = context.Arena.Scratch.Rent<int>(length: liveCount);
        using var valuesLease = context.Arena.Scratch.Rent<long>(length: liveCount);
        var heads = headsLease.Span;
        var tails = tailsLease.Span;
        var links = nextLease.Span;
        var values = valuesLease.Span;
        using var wordLease = context.Arena.Scratch.Rent<long>(length: (pool.Capacity + 1));
        using var moversLease = context.Arena.Scratch.Rent<StateInstanceHandle>(length: pool.Capacity);
        var word = wordLease.Span;
        var movers = moversLease.Span;

        // Snapshot order is slot order. Linking each cell's handles as they arrive preserves that order while
        // avoiding a complete pool scan for every cell the ray visits.
        for (var index = 0; (index < liveCount); index++) {
            if (!context.Arena.TryReadLiveRaw(fieldOrdinal: push.CellFieldOrdinal, handle: handles[index], raw: out var cell, time: context.Time) || (cell < 0L) || (cell >= push.Topology.CellCount)) {
                continue;
            }
            var indexedCell = ((int)cell);
            var entry = (index + 1);

            if (heads[indexedCell] == 0) {
                heads[indexedCell] = entry;
            } else {
                links[(tails[indexedCell] - 1)] = entry;
            }
            tails[indexedCell] = entry;
        }

        _ = context.Arena.TryReadLiveRaw(fieldOrdinal: push.ValueFieldOrdinal, handle: binding.Instance, raw: out var moverValue, time: context.Time);
        word[0] = moverValue;
        movers[0] = binding.Instance;
        var wordLength = 1;
        var moverCount = 1;
        var cellOrdinal = origin;
        var terminator = -1;
        Span<long> symbol = stackalloc long[1];

        for (var visited = 1; (visited <= push.Topology.CellCount); visited++) {
            cellOrdinal = push.Topology.Neighbour(cell: cellOrdinal, direction: push.Direction);
            if ((cellOrdinal < 0) || (visitedCells[cellOrdinal] != 0)) {
                return Refuse(code: TransformRefusal.PushRayBlocked, reason: "pushRay reached an edge or closed cycle before a passable terminator", refusal: out refusal);
            }
            visitedCells[cellOrdinal] = 1;

            var occupied = false;
            var hasPush = false;

            for (var entry = heads[cellOrdinal]; (entry != 0); entry = links[(entry - 1)]) {
                var index = (entry - 1);

                if (!context.Arena.TryReadLiveRaw(fieldOrdinal: push.ValueFieldOrdinal, handle: handles[index], raw: out var rawValue, time: context.Time)) {
                    return Refuse(code: TransformRefusal.PushRayAddressing, reason: "pushRay could not read a selected pool token", refusal: out refusal);
                }
                occupied = true;

                values[index] = rawValue;
                symbol[0] = rawValue;
                if (push.StopPattern.LongestAcceptedPrefix(values: symbol) == 1) {
                    return Refuse(code: TransformRefusal.PushRayBlocked, reason: "pushRay reached a stop occupant", refusal: out refusal);
                }
                hasPush |= (push.PushPattern.LongestAcceptedPrefix(values: symbol) == 1);
            }

            for (var entry = heads[cellOrdinal]; (entry != 0); entry = links[(entry - 1)]) {
                var index = (entry - 1);
                var rawValue = values[index];

                symbol[0] = rawValue;
                var selected = (push.PushPattern.LongestAcceptedPrefix(values: symbol) == 1);

                if (hasPush) {
                    if (!selected) { continue; }
                    movers[moverCount++] = handles[index];
                }
                word[wordLength++] = rawValue;
            }
            if (!hasPush) {
                terminator = cellOrdinal;
                if (!occupied) { word[wordLength++] = push.Empty; }
                break;
            }
        }

        if ((terminator < 0) || (push.Pattern.LongestAcceptedPrefix(values: word[..wordLength]) != wordLength)) {
            return Refuse(code: TransformRefusal.PushRayBlocked, reason: "pushRay's movable run and terminator do not match its pattern", refusal: out refusal);
        }

        for (var index = (moverCount - 1); (index >= 0); index--) {
            _ = context.Arena.TryReadLiveRaw(fieldOrdinal: push.CellFieldOrdinal, handle: movers[index], raw: out var current, time: context.Time);
            var destination = push.Topology.Neighbour(cell: checked((int)current), direction: push.Direction);
            var reason = "pushRay reached an invalid destination cell";

            if ((destination < 0) || !context.Arena.TryWriteLive(handle: movers[index], fieldOrdinal: push.CellFieldOrdinal, operand: destination, write: StateWriteKind.Set, time: context.Time, reason: out reason)) {
                return Refuse(code: TransformRefusal.PushRayBlocked, reason: reason, refusal: out refusal);
            }
            moved = true;
        }

        return Applied(refusal: out refusal);
    }
}
