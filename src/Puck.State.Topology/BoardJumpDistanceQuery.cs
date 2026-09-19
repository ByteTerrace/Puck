namespace Puck.State;

/// <summary>A non-capturing hop follows the same direction twice, over one occupied cell onto an empty
/// cell. Chains may change direction. The mover's source is vacated throughout the chain.</summary>
public sealed class BoardJumpDistanceQuery : BoardQuery {
    /// <param name="topology">The board's direction slots and adjacency.</param>
    /// <param name="target">The destination ordinal, unless resolved live.</param>
    /// <param name="targetIsLive">Whether the caller supplies the destination at evaluation time.</param>
    public BoardJumpDistanceQuery(CompiledTopology topology, int target, bool targetIsLive = false)
        : base(BoardQueryKind.JumpDistance, topology) {
        Target = target;
        TargetIsLive = targetIsLive;
    }

    /// <summary>Gets the literal destination ordinal.</summary>
    public int Target { get; }
    /// <summary>Gets whether the destination is read from live state.</summary>
    public bool TargetIsLive { get; }
    /// <inheritdoc/>
    public override long Visits => (long)Topology.CellCount * (2 * Topology.DirectionCount + 1);

    /// <summary>Returns the shortest chain length, zero for the source itself, or -1 for an invalid or
    /// unreachable destination. Each cell enters the queue once: O(cells × directions), including cycles.</summary>
    /// <param name="values">One occupancy value per topology cell.</param>
    /// <param name="empty">The board's unoccupied value.</param>
    /// <param name="source">The moving token's initial cell.</param>
    /// <param name="target">The resolved destination cell.</param>
    public long Evaluate(ReadOnlySpan<long> values, long empty, int source, int target) {
        var count = Topology.CellCount;
        if ((uint)source >= count || (uint)target >= count) {
            return -1;
        }
        if (source == target) {
            return 0;
        }
        if (values[target] != empty) {
            return -1;
        }
        var storage = System.Buffers.ArrayPool<int>.Shared.Rent(count * 2);
        try {
            var distance = storage.AsSpan(0, count);
            var queue = storage.AsSpan(count, count);
            distance.Fill(-1);
            distance[source] = 0;
            queue[0] = source;
            var tail = 1;
            for (var head = 0; head < tail; head++) {
                var cell = queue[head];
                for (var direction = 0; direction < Topology.DirectionCount; direction++) {
                    var middle = Topology.Neighbour(cell, direction);
                    if (middle < 0 || middle == source || values[middle] == empty) {
                        continue;
                    }
                    var landing = Topology.Neighbour(middle, direction);
                    if (landing < 0 || distance[landing] >= 0 || values[landing] != empty) {
                        continue;
                    }
                    distance[landing] = distance[cell] + 1;
                    if (landing == target) {
                        return distance[landing];
                    }
                    queue[tail++] = landing;
                }
            }
            return -1;
        } finally {
            System.Buffers.ArrayPool<int>.Shared.Return(storage);
        }
    }
}
