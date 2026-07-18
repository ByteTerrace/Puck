using Puck.Abstractions.Presentation;

namespace Puck.Recording.Session;

/// <summary>
/// A bounded single-producer/single-consumer ring of pre-allocated frame slots — the handoff from the render
/// thread (which copies each captured surface into a free slot and publishes it) to the one encode thread (which
/// drains slots, composites, and encodes). The render thread never blocks: when the ring is full it drops the
/// newest frame and counts it. Every slot buffer is rented once at construction, so steady state is
/// allocation-free.
/// </summary>
internal sealed class FrameSlotQueue {
    internal sealed class Slot {
        public required byte[] Pixels { get; init; }
        public int Width { get; set; }
        public int Height { get; set; }
        public SurfaceFormat Format { get; set; }
        public long TimestampNanoseconds { get; set; }
        public long SessionTimeNanoseconds { get; set; }
        public long SimTimeNanoseconds { get; set; }
        public int Length { get; set; }
    }

    private readonly int m_capacity;
    private long m_head;
    private readonly Slot[] m_slots;
    private long m_tail;

    /// <summary>Initializes a new instance of the <see cref="FrameSlotQueue"/> class.</summary>
    /// <param name="capacity">The number of slots.</param>
    /// <param name="slotBytes">The byte capacity of each slot's pixel buffer.</param>
    public FrameSlotQueue(int capacity, int slotBytes) {
        m_capacity = Math.Max(val1: 1, val2: capacity);
        m_slots = new Slot[m_capacity];

        for (var index = 0; (index < m_capacity); index++) {
            m_slots[index] = new Slot {
                Pixels = new byte[slotBytes],
            };
        }
    }

    /// <summary>Acquires the next free slot for the producer to fill, or <see langword="null"/> when the ring is full.</summary>
    /// <returns>A writable slot, or <see langword="null"/>.</returns>
    public Slot? TryAcquire() {
        if ((Volatile.Read(location: ref m_tail) - Volatile.Read(location: ref m_head)) >= m_capacity) {
            return null;
        }

        return m_slots[(int)(m_tail % m_capacity)];
    }

    /// <summary>Publishes the slot acquired by <see cref="TryAcquire"/> to the consumer.</summary>
    public void Publish() {
        Volatile.Write(location: ref m_tail, value: (m_tail + 1L));
    }

    /// <summary>Takes the next published slot for the consumer, or <see langword="null"/> when the ring is empty.</summary>
    /// <returns>A filled slot, or <see langword="null"/>.</returns>
    public Slot? TryTake() {
        if (Volatile.Read(location: ref m_head) >= Volatile.Read(location: ref m_tail)) {
            return null;
        }

        return m_slots[(int)(m_head % m_capacity)];
    }

    /// <summary>Releases the slot returned by <see cref="TryTake"/> back to the producer.</summary>
    public void Release() {
        Volatile.Write(location: ref m_head, value: (m_head + 1L));
    }
}
