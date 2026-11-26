using Puck.Maths;

namespace Puck.Abstractions.Memory;

/// <summary>
/// One owner's map from a 64-bit key to a reference-free value, held in <see cref="OwnedBuffer{T}"/> storage, which is
/// native, so a build's lookup table never reaches the collector. Open addressing with linear probing over a power-of-two table kept at most half full; a key's home slot is its
/// SplitMix64 mix (<see cref="InvertibleBitMix.Mix64"/>), so keys that differ in their high bits only (a lattice
/// coordinate packed into a word) spread over the table. Growth rehashes into a table twice the size. There is no
/// removal. Not thread-safe.
/// </summary>
/// <typeparam name="TValue">The value.</typeparam>
public sealed class OwnedLongMap<TValue> : IDisposable where TValue : unmanaged {
    private readonly IAllocator? m_allocator;
    private readonly IAllocationObserver? m_observer;
    private OwnedBuffer<long> m_keys;
    private OwnedBuffer<TValue> m_values;
    private OwnedBuffer<byte> m_used;
    private int m_mask;

    /// <summary>Initializes a new instance of the <see cref="OwnedLongMap{TValue}"/> class with room for
    /// <paramref name="capacity"/> keys before it grows.</summary>
    /// <param name="allocator">The native allocator, or <see langword="null"/> for the process's
    /// (<see cref="ProcessAllocator.Current"/>).</param>
    /// <param name="observer">The owning lifecycle's byte observer, or <see langword="null"/> for unmetered storage.</param>
    /// <param name="capacity">The keys to make room for at once; not negative.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is negative.</exception>
    public OwnedLongMap(IAllocator? allocator, IAllocationObserver? observer = null, int capacity = 0) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: capacity);
        m_allocator = allocator;
        m_observer = observer;
        var slots = SlotsFor(count: capacity);

        m_keys = Table<long>(slots: slots);
        m_values = Table<TValue>(slots: slots);
        m_used = Table<byte>(slots: slots, clear: true);
        m_mask = (slots - 1);
    }

    /// <summary>Gets the number of keys held.</summary>
    public int Count { get; private set; }

    /// <summary>Adds a key, or leaves the map unchanged when it already holds the key.</summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The value.</param>
    /// <returns><see langword="true"/> when the key was added.</returns>
    public bool TryAdd(long key, TValue value) {
        if ((2 * (Count + 1)) > (m_mask + 1)) { Grow(); }
        var used = m_used.GetSpan();
        var keys = m_keys.GetSpan();

        for (var slot = Home(key: key); ; slot = ((slot + 1) & m_mask)) {
            if (used[slot] == 0) {
                used[slot] = 1;
                keys[slot] = key;
                m_values[slot] = value;
                Count++;
                return true;
            }
            if (keys[slot] == key) { return false; }
        }
    }
    /// <summary>Adds a key the map does not hold.</summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The value.</param>
    /// <exception cref="ArgumentException">The map already holds <paramref name="key"/>.</exception>
    public void Add(long key, TValue value) {
        if (!TryAdd(key: key, value: value)) { throw new ArgumentException(message: $"The map already holds key {key}.", paramName: nameof(key)); }
    }
    /// <summary>Reads a key's value.</summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The value, when this returns <see langword="true"/>; the default otherwise.</param>
    /// <returns><see langword="true"/> when the map holds <paramref name="key"/>.</returns>
    public bool TryGetValue(long key, out TValue value) {
        var used = m_used.GetSpan();
        var keys = m_keys.GetSpan();

        for (var slot = Home(key: key); (used[slot] != 0); slot = ((slot + 1) & m_mask)) {
            if (keys[slot] == key) {
                value = m_values[slot];
                return true;
            }
        }
        value = default;
        return false;
    }
    /// <summary>Releases the map's storage and its charges.</summary>
    public void Dispose() {
        m_keys.Dispose();
        m_values.Dispose();
        m_used.Dispose();
        Count = 0;
    }

    private int Home(long key) => (int)(InvertibleBitMix.Mix64(value: (ulong)key) & (ulong)m_mask);
    // A power of two at least sixteen that keeps `count` keys at most half the table.
    private static int SlotsFor(int count) => (int)Math.Max(val1: 16UL, val2: (((ulong)Math.Max(val1: 1, val2: count)) * 2UL).NextPowerOfTwo());
    private OwnedBuffer<T> Table<T>(int slots, bool clear = false) where T : unmanaged => OwnedBuffer<T>.OfLength(allocator: m_allocator, clear: clear, length: slots, observer: m_observer);
    private void Grow() {
        var keys = m_keys;
        var values = m_values;
        var used = m_used;
        var slots = checked(2 * (m_mask + 1));

        m_keys = Table<long>(slots: slots);
        m_values = Table<TValue>(slots: slots);
        m_used = Table<byte>(slots: slots, clear: true);
        m_mask = (slots - 1);
        Count = 0;
        try {
            var held = used.GetSpan();
            var heldKeys = keys.GetSpan();
            var heldValues = values.GetSpan();

            for (var slot = 0; (slot < held.Length); slot++) {
                if (held[slot] != 0) { _ = TryAdd(key: heldKeys[slot], value: heldValues[slot]); }
            }
        } finally {
            keys.Dispose();
            values.Dispose();
            used.Dispose();
        }
    }
}
