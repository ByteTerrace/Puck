namespace Puck.Assets;

/// <summary>A fixed-capacity, least-recently-used cache keyed by <see cref="AssetContentHash"/>. Reading or
/// writing an entry marks it most-recently-used; once <see cref="Capacity"/> is exceeded the least-recently-used
/// entry is evicted.</summary>
/// <typeparam name="TValue">The cached value type.</typeparam>
public sealed class ContentAddressedLruCache<TValue> {
    private readonly OrderedDictionary<AssetContentHash, TValue> m_entries = [];
    private readonly Action<TValue>? m_onEvicted;

    /// <summary>Gets the maximum number of entries retained before eviction occurs.</summary>
    public int Capacity { get; }
    /// <summary>Gets the number of entries currently cached.</summary>
    public int Count => m_entries.Count;

    /// <summary>Initializes a new cache.</summary>
    /// <param name="capacity">The maximum number of entries to retain. Must be greater than zero.</param>
    /// <param name="onEvicted">An optional callback invoked with each value as it is evicted or replaced.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is not greater than zero.</exception>
    public ContentAddressedLruCache(int capacity, Action<TValue>? onEvicted = null) {
        if (capacity <= 0) {
            throw new ArgumentOutOfRangeException(
                message: "Cache capacity must be greater than zero.",
                paramName: nameof(capacity)
            );
        }

        Capacity = capacity;
        m_onEvicted = onEvicted;
    }

    /// <summary>Removes every entry, invoking the eviction callback for each.</summary>
    public void Clear() {
        if (m_onEvicted is not null) {
            foreach (var entry in m_entries) {
                m_onEvicted(entry.Value);
            }
        }

        m_entries.Clear();
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
    /// entry if capacity is exceeded.</summary>
    /// <param name="hash">The content hash to cache under.</param>
    /// <param name="value">The value to cache.</param>
    public void Set(AssetContentHash hash, TValue value) {
        if (m_entries.TryGetValue(
            key: hash,
            value: out var replacedValue
        )) {
            m_entries.Remove(key: hash);
            m_onEvicted?.Invoke(replacedValue);
            m_entries.Add(
                key: hash,
                value: value
            );
            return;
        }

        m_entries.Add(
            key: hash,
            value: value
        );
        while (m_entries.Count > Capacity) {
            var evictedValue = m_entries.GetAt(index: 0).Value;

            m_entries.RemoveAt(index: 0);
            m_onEvicted?.Invoke(evictedValue);
        }
    }
    /// <summary>Attempts to read the value cached for <paramref name="hash"/>, marking it most-recently-used.</summary>
    /// <param name="hash">The content hash to look up.</param>
    /// <param name="value">When this method returns <see langword="true"/>, the cached value.</param>
    /// <returns><see langword="true"/> if an entry was found; otherwise <see langword="false"/>.</returns>
    public bool TryGet(AssetContentHash hash, out TValue value) {
        if (!m_entries.TryGetValue(
            key: hash,
            value: out value!
        )) {
            return false;
        }

        m_entries.Remove(key: hash);
        m_entries.Add(
            key: hash,
            value: value
        );
        return true;
    }
}
