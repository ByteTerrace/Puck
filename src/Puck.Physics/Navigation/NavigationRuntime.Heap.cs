using Puck.Abstractions.Collections;

namespace Puck.Physics.Navigation;

/// <summary>The indexed binary min-heap both route searches — a domain's per-body A* and its shared destination
/// trees — schedule open nodes through: node indices in <see cref="BinaryHeap"/> order, and each node's heap slot in a
/// position table so a cheaper rediscovery sifts in place rather than re-inserting.</summary>
/// <remarks>The order is a struct <see cref="IComparer{T}"/> over node indices that breaks every tie itself (by node
/// index), never the heap, so a search stays deterministic. The position table is never cleared:
/// <see cref="Contains"/> is meaningful only for a node the caller has stamped as discovered in the current search (a
/// slot left by an earlier search reads as stale). A caller that marks such a node settled without popping it calls
/// <see cref="Forget"/>.</remarks>
internal sealed class NodeHeap {
    private readonly int[] m_heap;
    private readonly int[] m_position;

    private int m_count;

    /// <summary>Initializes a heap sized for <paramref name="cells"/> nodes.</summary>
    /// <param name="cells">The node count.</param>
    public NodeHeap(int cells) {
        m_heap = new int[cells];
        m_position = new int[cells];
    }

    /// <summary>Gets the number of queued nodes.</summary>
    public int Count => m_count;

    /// <summary>Empties the heap.</summary>
    public void Clear() => m_count = 0;
    /// <summary>Returns a value indicating whether a node discovered in the current search is still queued.</summary>
    /// <param name="node">The node.</param>
    public bool Contains(int node) => (m_position[node] >= 0);
    /// <summary>Re-sifts a queued node whose key just decreased.</summary>
    /// <typeparam name="TOrder">The order.</typeparam>
    /// <param name="node">The queued node.</param>
    /// <param name="order">The order.</param>
    public void Decrease<TOrder>(int node, TOrder order) where TOrder : struct, IComparer<int> {
        var slots = new Positions(m_position);

        BinaryHeap.Decrease(heap: m_heap.AsSpan(start: 0, length: m_count), index: m_position[node], order: order, slots: ref slots);
    }
    /// <summary>Marks a node as not queued without popping it.</summary>
    /// <param name="node">The node.</param>
    public void Forget(int node) => m_position[node] = -1;
    /// <summary>Removes and returns the first node in <paramref name="order"/>.</summary>
    /// <typeparam name="TOrder">The order.</typeparam>
    /// <param name="order">The order.</param>
    public int Pop<TOrder>(TOrder order) where TOrder : struct, IComparer<int> {
        var slots = new Positions(m_position);
        var result = BinaryHeap.Pop(count: ref m_count, heap: m_heap, order: order, slots: ref slots);

        m_position[result] = -1;
        return result;
    }
    /// <summary>Queues a node not currently in the heap.</summary>
    /// <typeparam name="TOrder">The order.</typeparam>
    /// <param name="node">The node.</param>
    /// <param name="order">The order.</param>
    public void Push<TOrder>(int node, TOrder order) where TOrder : struct, IComparer<int> {
        var slots = new Positions(m_position);

        BinaryHeap.Push(count: ref m_count, heap: m_heap, item: node, order: order, slots: ref slots);
    }

    private readonly struct Positions(int[] position) : IBinaryHeapSlots<int> {
        public void Moved(int item, int slot) => position[item] = slot;
    }
}
