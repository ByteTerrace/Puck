namespace Puck.Physics.Navigation;

/// <summary>A total order over node indices a <see cref="NodeHeap"/> pops by. Implemented by a
/// <see langword="readonly"/> struct so the heap's generic instantiation inlines the comparison and allocates
/// nothing; the order breaks every tie itself (by node index), never the heap, so a search stays deterministic.</summary>
internal interface INodeOrder {
    /// <summary>Compares two nodes; negative when <paramref name="left"/> pops first.</summary>
    /// <param name="left">The first node.</param>
    /// <param name="right">The second node.</param>
    int Compare(int left, int right);
}

/// <summary>The indexed binary min-heap both route searches — a domain's per-body A* and its shared destination
/// trees — schedule open nodes through: node indices in heap order, and each node's heap slot in a position table so
/// a cheaper rediscovery sifts in place rather than re-inserting.</summary>
/// <remarks>The position table is never cleared: <see cref="Contains"/> is meaningful only for a node the caller
/// has stamped as discovered in the current search (a slot left by an earlier search reads as stale). A caller that
/// marks such a node settled without popping it calls <see cref="Forget"/>.</remarks>
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
    public void Decrease<TOrder>(int node, TOrder order) where TOrder : struct, INodeOrder => SiftUp(
        index: m_position[node],
        order: order
    );
    /// <summary>Marks a node as not queued without popping it.</summary>
    /// <param name="node">The node.</param>
    public void Forget(int node) => m_position[node] = -1;
    /// <summary>Removes and returns the first node in <paramref name="order"/>.</summary>
    /// <typeparam name="TOrder">The order.</typeparam>
    /// <param name="order">The order.</param>
    public int Pop<TOrder>(TOrder order) where TOrder : struct, INodeOrder {
        var result = m_heap[0];
        var last = m_heap[--m_count];

        if (m_count != 0) {
            m_heap[0] = last;
            m_position[last] = 0;
            SiftDown(
                index: 0,
                order: order
            );
        }

        m_position[result] = -1;

        return result;
    }
    /// <summary>Queues a node not currently in the heap.</summary>
    /// <typeparam name="TOrder">The order.</typeparam>
    /// <param name="node">The node.</param>
    /// <param name="order">The order.</param>
    public void Push<TOrder>(int node, TOrder order) where TOrder : struct, INodeOrder {
        m_position[node] = m_count;
        m_heap[m_count++] = node;
        SiftUp(
            index: m_position[node],
            order: order
        );
    }

    private void SiftDown<TOrder>(int index, TOrder order) where TOrder : struct, INodeOrder {
        while (true) {
            var left = ((index * 2) + 1);

            if (left >= m_count) {
                break;
            }

            var right = (left + 1);
            var best = (((right < m_count) && (order.Compare(left: m_heap[right], right: m_heap[left]) < 0))
                ? right
                : left
            );

            if (order.Compare(left: m_heap[best], right: m_heap[index]) >= 0) {
                break;
            }

            Swap(
                left: index,
                right: best
            );
            index = best;
        }
    }
    private void SiftUp<TOrder>(int index, TOrder order) where TOrder : struct, INodeOrder {
        while (index > 0) {
            var parent = ((index - 1) >> 1);

            if (order.Compare(left: m_heap[index], right: m_heap[parent]) >= 0) {
                break;
            }

            Swap(
                left: index,
                right: parent
            );
            index = parent;
        }
    }
    private void Swap(int left, int right) {
        (m_heap[left], m_heap[right]) = (m_heap[right], m_heap[left]);
        m_position[m_heap[left]] = left;
        m_position[m_heap[right]] = right;
    }
}
