using System.Numerics;
using Puck.Abstractions.Memory;
using Puck.Maths;

namespace Puck.SdfVm;

// The first vertex at each position in vertex order, in an open-addressed table over native storage (linear probing,
// kept at most half full, doubled by a rehash) so a weld never reaches the collector. Two positions are one place when
// every component is equal as float.Equals reads it (zeros of either sign, NaNs of any payload), and the hash agrees.
internal sealed class SdfMeshWeld : IDisposable {
    private const int Empty = -1;

    private readonly IAllocationObserver? m_observer;
    private readonly IAllocator? m_allocator;
    private OwnedBuffer<Entry> m_entries;
    private int m_count;
    private int m_mask;

    // The place a vertex first stood at; Vertex is Empty for an unused slot.
    private readonly record struct Entry(Vector3 Position, int Vertex);

    public SdfMeshWeld(int capacity, IAllocator? allocator = null, IAllocationObserver? owner = null) {
        m_allocator = allocator;
        m_observer = owner;
        m_entries = Table(slots: SlotsFor(count: capacity));
        m_mask = (m_entries.Length - 1);
    }

    public int Site(Vector3 position, int vertex) {
        if ((2 * (m_count + 1)) > (m_mask + 1)) { Grow(); }

        var entries = m_entries.GetSpan();

        for (var slot = Home(position: position); ; slot = ((slot + 1) & m_mask)) {
            ref var entry = ref entries[slot];

            if (entry.Vertex == Empty) {
                entry = new Entry(Position: position, Vertex: vertex);
                m_count++;

                return vertex;
            }
            if (Same(a: entry.Position, b: position)) { return entry.Vertex; }
        }
    }

    public bool TryFind(Vector3 position, out int vertex) {
        var entries = m_entries.GetSpan();

        for (var slot = Home(position: position); ; slot = ((slot + 1) & m_mask)) {
            var entry = entries[slot];

            if (entry.Vertex == Empty) {
                vertex = 0;

                return false;
            }
            if (Same(a: entry.Position, b: position)) {
                vertex = entry.Vertex;

                return true;
            }
        }
    }

    public void Dispose() => m_entries.Dispose();

    public static int[] Sites(ReadOnlySpan<Vector3> positions) {
        using var weld = new SdfMeshWeld(positions.Length);
        var sites = new int[positions.Length];

        for (var vertex = 0; vertex < sites.Length; vertex++) { sites[vertex] = weld.Site(positions[vertex], vertex); }

        return sites;
    }

    private static bool Same(Vector3 a, Vector3 b) => (a.X.Equals(obj: b.X) && a.Y.Equals(obj: b.Y) && a.Z.Equals(obj: b.Z));
    // A component's bits with every equal value on one pattern: negative zero as zero, every NaN as one.
    private static uint Bits(float value) => (float.IsNaN(value) ? 0x7FC00000u : BitConverter.SingleToUInt32Bits(value: (value + 0f)));
    private int Home(Vector3 position) =>
        (int)(InvertibleBitMix.Mix64(value: (InvertibleBitMix.Mix64(value: ((((ulong)Bits(value: position.X)) << 32) | Bits(value: position.Y))) ^ Bits(value: position.Z))) & ((ulong)m_mask));
    // A power of two at least sixteen that keeps `count` entries at most half the table.
    private static int SlotsFor(int count) => (int)Math.Max(val1: 16UL, val2: (((ulong)Math.Max(val1: 1, val2: count)) * 2UL).NextPowerOfTwo());
    private OwnedBuffer<Entry> Table(int slots) {
        var table = OwnedBuffer<Entry>.OfLength(allocator: m_allocator, clear: false, length: slots, observer: m_observer);

        table.GetSpan().Fill(value: new Entry(Position: default, Vertex: Empty));

        return table;
    }
    private void Grow() {
        var held = m_entries;
        var slots = checked(2 * (m_mask + 1));

        m_entries = Table(slots: slots);
        m_mask = (slots - 1);
        m_count = 0;
        try {
            foreach (var entry in held.GetSpan()) {
                if (entry.Vertex != Empty) { _ = Site(position: entry.Position, vertex: entry.Vertex); }
            }
        } finally {
            held.Dispose();
        }
    }
}
