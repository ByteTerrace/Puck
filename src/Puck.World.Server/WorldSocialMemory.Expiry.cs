namespace Puck.World.Server;

public sealed partial class WorldSocialMemory {
    // Index by the receipt's recyclable owner-list node, not its (possibly exhausted) logical admission ordinal.
    // Exactly one heap entry exists per unfrozen receipt. Frozen owners retain their receipts outside this index,
    // so an indefinitely unresolved export cannot block reclamation for everyone else. Ownership retirement never scans the heap or
    // leaves tombstones that could accumulate across crossings. Like the owner links, this is derived metadata.
    private sealed class ReceiptExpiry {
        private readonly record struct Entry(int Node, Int128 Tick, ulong Ordinal);
        private readonly Entry[] m_heap;
        private readonly int[] m_positions;
        private int m_count;

        public ReceiptExpiry(int capacity) {
            m_heap = new Entry[capacity];
            m_positions = new int[capacity];
            Array.Fill(m_positions, -1);
        }

        public bool TryPeek(out int node, out Int128 tick) {
            if (m_count == 0) { node = -1; tick = 0; return false; }
            node = m_heap[0].Node;
            tick = m_heap[0].Tick;
            return true;
        }

        public void Add(int node, Int128 tick, ulong ordinal) {
            if (m_count == m_heap.Length || m_positions[node] >= 0) {
                throw new InvalidOperationException("social receipt expiry index exceeded its reserved capacity or duplicated a node");
            }
            SiftUp(m_count++, new(node, tick, ordinal));
        }

        public void Remove(int node) {
            var index = m_positions[node];
            if (index < 0) { throw new InvalidOperationException("social receipt expiry index lost a retained node"); }
            m_positions[node] = -1;
            var last = m_heap[--m_count];
            m_heap[m_count] = default;
            if (index == m_count) { return; }
            if (index > 0 && Compare(last, m_heap[(index - 1) / 4]) < 0) { SiftUp(index, last); }
            else { SiftDown(index, last); }
        }

        private void SiftUp(int index, Entry entry) {
            while (index > 0) {
                var parent = (index - 1) / 4;
                if (Compare(entry, m_heap[parent]) >= 0) { break; }
                Place(index, m_heap[parent]);
                index = parent;
            }
            Place(index, entry);
        }

        private void SiftDown(int index, Entry entry) {
            // Policy bounds capacity far below Int32.MaxValue / 4, so child-index multiplication cannot overflow.
            for (var child = (index * 4) + 1; child < m_count; child = (index * 4) + 1) {
                var best = child;
                var end = Math.Min(child + 4, m_count);
                for (var sibling = child + 1; sibling < end; sibling++) {
                    if (Compare(m_heap[sibling], m_heap[best]) < 0) { best = sibling; }
                }
                if (Compare(entry, m_heap[best]) <= 0) { break; }
                Place(index, m_heap[best]);
                index = best;
            }
            Place(index, entry);
        }

        private void Place(int index, Entry entry) {
            m_heap[index] = entry;
            m_positions[entry.Node] = index;
        }

        private static int Compare(Entry left, Entry right) {
            var time = left.Tick.CompareTo(right.Tick);
            return time != 0 ? time : left.Ordinal.CompareTo(right.Ordinal);
        }
    }
}
