namespace Puck.Assets;

/// <summary>A fixed-capacity, least-recently-used cache keyed by <see cref="AssetContentHash"/>. Reading or
/// writing an entry marks it most-recently-used. Both entry capacity and optional retained weight are enforced
/// by least-recently-used eviction. Values larger than the weight budget are not retained.</summary>
/// <typeparam name="TValue">The cached value type.</typeparam>
public sealed class ContentAddressedLruCache<TValue> {
    private readonly OrderedDictionary<AssetContentHash, (TValue Value, long Weight)> m_entries = [];
    private readonly Action<TValue>? m_onEvicted;
    private readonly Func<TValue, long>? m_getWeight;

    /// <summary>Gets the maximum retained weight, in the units supplied by the caller.</summary>
    public long WeightCapacity { get; }
    /// <summary>Gets the sum of the weights recorded at insertion for retained entries.</summary>
    public long Weight { get; private set; }

    /// <summary>Gets the maximum number of entries retained before eviction occurs.</summary>
    public int Capacity { get; }
    /// <summary>Gets the number of entries currently cached.</summary>
    public int Count => m_entries.Count;

    /// <summary>Initializes a new cache.</summary>
    /// <param name="capacity">The maximum number of entries to retain. Must be greater than zero.</param>
    /// <param name="onEvicted">An optional callback invoked with each value as it is evicted or replaced.</param>
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
        if (m_onEvicted is not null) {
            foreach (var entry in m_entries) {
                m_onEvicted(entry.Value.Value);
            }
        }

        m_entries.Clear();
        Weight = 0;
    }
    /// <summary>Returns the value cached for <paramref name="hash"/>, producing and caching it with
    /// <paramref name="valueFactory"/> on a miss.</summary>
    /// <param name="hash">The content hash to look up.</param>
    /// <param name="valueFactory">The factory invoked to produce the value on a cache miss.</param>
    /// <returns>The cached or newly produced value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="valueFactory"/> is <see langword="null"/>.</exception>
    public TValue GetOrAdd(AssetContentHash hash, Func<TValue> valueFactory) {
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
    /// <summary>Caches <paramref name="value"/> under <paramref name="hash"/>, evicting the least-recently-used
    /// entries as needed to satisfy both budgets. An oversized replacement removes the previous value but
    /// is not retained; other entries are left alone. Weights are measured once on insertion.</summary>
    /// <param name="hash">The content hash to cache under.</param>
    /// <param name="value">The value to cache.</param>
    /// <exception cref="ArgumentOutOfRangeException">The weight selector returns a negative weight.</exception>
    public void Set(AssetContentHash hash, TValue value) {
        var weight = m_getWeight?.Invoke(value) ?? 0;
        ArgumentOutOfRangeException.ThrowIfNegative(weight);

        if (m_entries.TryGetValue(
            key: hash,
            value: out var replacedValue
        )) {
            m_entries.Remove(key: hash);
            Weight -= replacedValue.Weight;
            m_onEvicted?.Invoke(replacedValue.Value);
        }

        if (weight > WeightCapacity) {
            return;
        }

        // Subtraction avoids overflowing when the admitted weight is close to Int64.MaxValue.
        while ((m_entries.Count >= Capacity) || (Weight > (WeightCapacity - weight))) {
            var evicted = m_entries.GetAt(index: 0);

            m_entries.RemoveAt(index: 0);
            Weight -= evicted.Value.Weight;
            m_onEvicted?.Invoke(evicted.Value.Value);
        }

        m_entries.Add(hash, (value, weight));
        Weight += weight;
    }
    /// <summary>Attempts to read the value cached for <paramref name="hash"/>, marking it most-recently-used.</summary>
    /// <param name="hash">The content hash to look up.</param>
    /// <param name="value">When this method returns <see langword="true"/>, the cached value.</param>
    /// <returns><see langword="true"/> if an entry was found; otherwise <see langword="false"/>.</returns>
    public bool TryGet(AssetContentHash hash, out TValue value) {
        if (!m_entries.TryGetValue(
            key: hash,
            value: out var entry
        )) {
            value = default!;
            return false;
        }

        m_entries.Remove(key: hash);
        m_entries.Add(
            key: hash,
            value: entry
        );
        value = entry.Value;
        return true;
    }
}
