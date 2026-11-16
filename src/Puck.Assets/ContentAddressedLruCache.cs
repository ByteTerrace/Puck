namespace Puck.Assets;

/// <summary>A fixed-capacity, least-recently-used cache keyed by a content identity. Reading or
/// writing an entry marks it most-recently-used. Both entry capacity and optional retained weight are enforced
/// by least-recently-used eviction. Values larger than the weight budget are not retained. The cache is not
/// thread-safe; a caller that shares one serializes its calls.</summary>
/// <typeparam name="TKey">The content identity type. Security-sensitive callers use <see cref="ContentPin"/>.</typeparam>
/// <typeparam name="TValue">The cached value type.</typeparam>
public sealed class ContentAddressedLruCache<TKey, TValue> where TKey : notnull {
    private readonly Dictionary<TKey, LinkedListNode<Entry>> m_entries = [];
    private readonly Func<TValue, long>? m_getWeight;
    // Least recently used first.
    private readonly LinkedList<Entry> m_order = new();
    private readonly Action<TValue>? m_onEvicted;

    /// <summary>Gets the maximum number of entries retained before eviction occurs.</summary>
    public int Capacity { get; }
    /// <summary>Gets the number of entries currently cached.</summary>
    public int Count => m_entries.Count;
    /// <summary>Gets the sum of the weights recorded for retained entries.</summary>
    public long Weight { get; private set; }
    /// <summary>Gets the maximum retained weight, in the units supplied by the caller.</summary>
    public long WeightCapacity { get; }

    /// <summary>Initializes a new cache.</summary>
    /// <param name="capacity">The maximum number of entries to retain. Must be greater than zero.</param>
    /// <param name="onEvicted">An optional callback invoked with each value as it is evicted, removed or replaced.</param>
    /// <param name="getWeight">An optional non-negative retained-weight selector. An oversized value is returned by GetOrAdd but not cached.</param>
    /// <param name="weightCapacity">The maximum retained weight. Must be non-negative. Entry capacity is enforced independently.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is not greater than zero or <paramref name="weightCapacity"/> is negative.</exception>
    public ContentAddressedLruCache(int capacity, Action<TValue>? onEvicted = null, Func<TValue, long>? getWeight = null, long weightCapacity = long.MaxValue) {
        if (capacity <= 0) {
            throw new ArgumentOutOfRangeException(
                message: "Cache capacity must be greater than zero.",
                paramName: nameof(capacity)
            );
        }

        Capacity = capacity;
        m_onEvicted = onEvicted;
        ArgumentOutOfRangeException.ThrowIfNegative(weightCapacity);
        WeightCapacity = weightCapacity;
        m_getWeight = getWeight;
    }

    /// <summary>Removes every entry, invoking the eviction callback for each.</summary>
    public void Clear() {
        var entries = m_order.ToArray();

        m_entries.Clear();
        m_order.Clear();
        Weight = 0;
        if (m_onEvicted is not null) {
            foreach (var entry in entries) {
                m_onEvicted(entry.Value);
            }
        }
    }
    /// <summary>Indicates whether an entry is cached for <paramref name="hash"/>, without marking it most-recently-used.</summary>
    /// <param name="hash">The content hash to look up.</param>
    /// <returns><see langword="true"/> if an entry was found; otherwise <see langword="false"/>.</returns>
    public bool Contains(TKey hash) => m_entries.ContainsKey(key: hash);
    /// <summary>Returns the value cached for <paramref name="hash"/>, producing and caching it with
    /// <paramref name="valueFactory"/> on a miss.</summary>
    /// <param name="hash">The content hash to look up.</param>
    /// <param name="valueFactory">The factory invoked to produce the value on a cache miss.</param>
    /// <returns>The cached or newly produced value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="valueFactory"/> is <see langword="null"/>.</exception>
    public TValue GetOrAdd(TKey hash, Func<TValue> valueFactory) {
        ArgumentNullException.ThrowIfNull(valueFactory);

        if (TryGet(
            hash: hash,
            value: out var existingValue
        )) {
            return existingValue;
        }

        var value = valueFactory();

        Set(
            hash: hash,
            value: value
        );
        return value;
    }
    /// <summary>Removes the entry cached for <paramref name="hash"/>, invoking the eviction callback with its value.</summary>
    /// <param name="hash">The content hash to remove.</param>
    /// <returns><see langword="true"/> if an entry was removed; otherwise <see langword="false"/>.</returns>
    public bool Remove(TKey hash) {
        if (!m_entries.Remove(
            key: hash,
            value: out var node
        )) {
            return false;
        }

        m_order.Remove(node: node);
        Weight -= node.Value.Weight;
        m_onEvicted?.Invoke(node.Value.Value);

        return true;
    }
    /// <summary>Adds to the weight recorded for the entry cached under <paramref name="hash"/>, as its value grows or
    /// shrinks after insertion, and marks it most-recently-used. Nothing is evicted; <see cref="TrimTo"/> brings the cache
    /// back under a budget.</summary>
    /// <param name="hash">The content hash of the entry.</param>
    /// <param name="delta">The change to its weight, which may be negative; the result must not be negative.</param>
    /// <returns><see langword="true"/> if the entry was found; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The entry's weight would become negative.</exception>
    public bool Reweigh(TKey hash, long delta) {
        if (!m_entries.TryGetValue(
            key: hash,
            value: out var node
        )) {
            return false;
        }

        var weight = checked(node.Value.Weight + delta);

        ArgumentOutOfRangeException.ThrowIfNegative(weight);
        node.Value = (node.Value with { Weight = weight });
        Weight = checked(Weight + delta);
        MarkUsed(node: node);

        return true;
    }
    /// <summary>Caches <paramref name="value"/> under <paramref name="hash"/>, evicting the least-recently-used
    /// entries as needed to satisfy both budgets. An oversized replacement removes the previous value but
    /// is not retained; other entries are left alone. Weights are measured once on insertion
    /// (<see cref="Reweigh"/> changes one afterwards).</summary>
    /// <param name="hash">The content hash to cache under.</param>
    /// <param name="value">The value to cache.</param>
    /// <exception cref="ArgumentOutOfRangeException">The weight selector returns a negative weight.</exception>
    public void Set(TKey hash, TValue value) {
        var weight = (m_getWeight?.Invoke(value) ?? 0);

        ArgumentOutOfRangeException.ThrowIfNegative(weight);

        if (m_entries.Remove(
            key: hash,
            value: out var replaced
        )) {
            m_order.Remove(node: replaced);
            Weight -= replaced.Value.Weight;
            m_onEvicted?.Invoke(replaced.Value.Value);
        }

        if (weight > WeightCapacity) {
            return;
        }

        // Subtraction avoids overflowing when the admitted weight is close to Int64.MaxValue.
        while (
            (m_entries.Count >= Capacity) ||
            (Weight > (WeightCapacity - weight))
        ) {
            EvictOldest();
        }

        m_entries.Add(
            key: hash,
            value: m_order.AddLast(value: new Entry(Key: hash, Value: value, Weight: weight))
        );
        Weight += weight;
    }
    /// <summary>Evicts the least-recently-used entries until the retained weight is within a budget, invoking the
    /// eviction callback for each. The cache's own <see cref="WeightCapacity"/> is enforced on insertion; this serves a
    /// caller whose budget changes between insertions.</summary>
    /// <param name="weight">The weight to trim to; non-negative.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="weight"/> is negative.</exception>
    public void TrimTo(long weight) {
        ArgumentOutOfRangeException.ThrowIfNegative(weight);

        while (
            (Weight > weight) &&
            (m_order.Count > 0)
        ) {
            EvictOldest();
        }
    }
    /// <summary>Attempts to read the value cached for <paramref name="hash"/>, marking it most-recently-used.</summary>
    /// <param name="hash">The content hash to look up.</param>
    /// <param name="value">When this method returns <see langword="true"/>, the cached value.</param>
    /// <returns><see langword="true"/> if an entry was found; otherwise <see langword="false"/>.</returns>
    public bool TryGet(TKey hash, out TValue value) {
        if (!m_entries.TryGetValue(
            key: hash,
            value: out var node
        )) {
            value = default!;
            return false;
        }

        MarkUsed(node: node);
        value = node.Value.Value;
        return true;
    }
    /// <summary>Attempts to read the value cached for <paramref name="hash"/> without marking it most-recently-used.</summary>
    /// <param name="hash">The content hash to look up.</param>
    /// <param name="value">When this method returns <see langword="true"/>, the cached value.</param>
    /// <returns><see langword="true"/> if an entry was found; otherwise <see langword="false"/>.</returns>
    public bool TryPeek(TKey hash, out TValue value) {
        if (!m_entries.TryGetValue(
            key: hash,
            value: out var node
        )) {
            value = default!;
            return false;
        }

        value = node.Value.Value;
        return true;
    }

    private void EvictOldest() {
        var oldest = m_order.First!;

        m_order.RemoveFirst();
        _ = m_entries.Remove(key: oldest.Value.Key);
        Weight -= oldest.Value.Weight;
        m_onEvicted?.Invoke(oldest.Value.Value);
    }
    private void MarkUsed(LinkedListNode<Entry> node) {
        if (node.Next is null) {
            return;
        }

        m_order.Remove(node: node);
        m_order.AddLast(node: node);
    }

    private readonly record struct Entry(TKey Key, TValue Value, long Weight);
}
