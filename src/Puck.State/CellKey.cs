using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using Puck.Abstractions;
using Puck.Abstractions.Memory;
using Puck.Maths;

namespace Puck.State;

/// <summary>A cell key interned by one <see cref="CellKeyTable"/> — the runtime address a compiled read or write
/// carries in place of the authored string. Row names keep <see cref="CellName"/>; only keys intern.</summary>
/// <remarks>Compiled keys also resolve in arenas built from their catalog. Runtime keys belong to one arena and
/// cease to resolve when their mint is rewound. The default value is invalid.</remarks>
public readonly record struct CellKey {
    private readonly ulong m_address;
    private readonly object? m_tableIdentity;

    internal CellKey(int ordinal, object tableIdentity, uint generation = 0) {
        m_address = PackedIntPair.Pack(high: generation, low: ((uint)checked((ordinal + 1))));
        m_tableIdentity = tableIdentity;
    }

    /// <summary>Gets a value indicating whether this key was minted by a key table.</summary>
    public bool IsValid => (PackedIntPair.Low(value: m_address) != 0);
    /// <summary>Gets the key's stable intern ordinal, or <c>-1</c> for the default invalid key.</summary>
    public int Ordinal => (((int)PackedIntPair.Low(value: m_address)) - 1);

    internal bool BelongsTo(object tableIdentity) => ReferenceEquals(
        objA: m_tableIdentity,
        objB: tableIdentity
    );
}
/// <summary>
/// An intern table for compiled symbols or one arena's runtime keys. An arena starts with the catalog's authored keys,
/// then owns its appended names and releases speculative additions on rewind.
/// </summary>
/// <remarks>Interning is global to the table, not per row: one name used by many rows occupies one ordinal. The
/// table is not synchronized. The owning compiler or arena thread performs every mutation. Runtime ordinals are
/// addresses, not persistent identities; content hashing uses names.</remarks>
public sealed class CellKeyTable {
    private static readonly ulong AbsentNameDigest = NameDigest(name: null);

    private readonly Dictionary<string, CellKey> m_byName;
    private readonly object m_identity;
    private readonly List<CellName> m_names;
    private readonly ReadOnlyCollection<CellName> m_readOnlyNames;
    private readonly List<CellKey> m_keys;
    private readonly CellKeyTable? m_source;
    private readonly int m_initialCount;

    private object m_runtimeIdentity = new();
    private uint m_generation;
    private int m_seedCount;

    private readonly List<ulong> m_nameDigests;

    private Func<long>? m_byteBudget;
    private long m_bytes;
    private MultisetDigest m_ledger;

    // The local keys whose names spell an index below StateCapacity.MaxCellsPerRow in canonical decimal, by index,
    // the invalid default where no local key does. A derived view of local membership, kept by the only doors that
    // change it (TryIntern and the fork add, Rewind releases), so a read never writes it.
    private CellKey[] m_indexKeys = [];
    // Each local key's integer (CanonicalDecimal over its name), NoInteger where the name spells none, by ordinal: a
    // derived view of m_names kept by the same doors, so a read takes an integer without parsing. TryKeyInteger is the
    // one reader, and it parses again only for NoInteger, which long.MinValue's own spelling also stores.
    private readonly List<long> m_integers;
    private const long NoInteger = long.MinValue;

    /// <summary>Initializes an empty table.</summary>
    public CellKeyTable() {
        m_byName = new Dictionary<string, CellKey>(comparer: StringComparer.Ordinal);
        m_identity = new object();
        m_names = [];
        m_keys = [];
        m_nameDigests = [];
        m_integers = [];
        m_readOnlyNames = m_names.AsReadOnly();
    }

    private CellKeyTable(CellKeyTable source) {
        m_source = source;
        m_identity = source.m_identity;
        m_initialCount = source.m_seedCount;
        m_names = source.m_names.GetRange(count: m_initialCount, index: 0);
        m_keys = source.m_keys.GetRange(count: m_initialCount, index: 0);
        m_nameDigests = source.m_nameDigests.GetRange(count: m_initialCount, index: 0);
        m_integers = source.m_integers.GetRange(count: m_initialCount, index: 0);
        m_byName = new Dictionary<string, CellKey>(capacity: m_initialCount, comparer: StringComparer.Ordinal);
        for (var index = 0; (index < m_initialCount); index++) {
            m_byName.Add(key: m_names[index].Value, value: m_keys[index]);
            m_bytes += EntryBytes(name: m_names[index]);
            m_ledger = m_ledger.Add(element: m_nameDigests[index]);
            NoteIndex(key: m_keys[index], integer: m_integers[index]);
        }
        m_readOnlyNames = m_names.AsReadOnly();
    }

    // Records a local key in the index view when its name spells an index below IndexKeyCache's bound.
    private void NoteIndex(CellKey key, long integer) {
        if (((ulong)integer) >= ((ulong)StateCapacity.MaxCellsPerRow)) {
            return;
        }
        if (integer >= m_indexKeys.Length) {
            Array.Resize(ref m_indexKeys, ArrayCapacity.Grown(m_indexKeys.Length, ((int)integer) + 1, ceiling: StateCapacity.MaxCellsPerRow));
        }
        m_indexKeys[integer] = key;
    }

    internal CellKeyTable Fork() => new(source: this);
    // Only declaration-owned names seed arenas. Rule/search literals bound after catalog construction remain
    // compiler symbols until a write admits them, independent of when a particular arena was constructed.
    internal void SealSeed() => m_seedCount = m_names.Count;
    internal bool ContainsLocal(CellName name) => m_byName.ContainsKey(key: name.Value);
    internal void SetByteBudget(Func<long> budget) => m_byteBudget = budget;
    internal static long EntryBytes(CellName name) => (96L + (2L * name.Value.Length));
    internal bool TryAdmitIntern(CellName name, out string reason) {
        if (ContainsLocal(name: name)) {
            reason = string.Empty;
            return true;
        }
        if (m_names.Count >= StateCapacity.MaxCellKeys) {
            reason = $"Cell key '{name.Value}' cannot be interned: the table already holds {StateCapacity.MaxCellKeys} distinct keys.";
            return false;
        }
        if ((m_bytes + EntryBytes(name: name)) > (m_byteBudget?.Invoke() ?? ArenaCapacity.MaxBytes)) {
            reason = $"Cell key '{name.Value}' cannot be interned: its retained storage exceeds the arena byte budget.";
            return false;
        }
        reason = string.Empty;
        return true;
    }
    internal void Rewind(int count) {
        if ((count < m_initialCount) || (count > m_names.Count)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(count));
        }
        if (count == m_names.Count) {
            return;
        }
        for (var index = count; (index < m_names.Count); index++) {
            m_bytes -= EntryBytes(name: m_names[index]);
            m_ledger = m_ledger.Remove(element: m_nameDigests[index]);
            m_byName.Remove(key: m_names[index].Value);
            if (((ulong)m_integers[index]) < ((ulong)m_indexKeys.Length)) {
                m_indexKeys[m_integers[index]] = default;
            }
        }
        m_nameDigests.RemoveRange(index: count, count: (m_nameDigests.Count - count));
        m_integers.RemoveRange(index: count, count: (m_integers.Count - count));
        m_keys.RemoveRange(index: count, count: (m_keys.Count - count));
        m_names.RemoveRange(index: count, count: (m_names.Count - count));
        // A reused ordinal gets a new generation without allocating an identity object. Renew the identity
        // only after all 2^32 generations have been used, so counter rollover cannot revive an ancient handle.
        if (m_generation < uint.MaxValue) {
            m_generation++;
        } else {
            m_runtimeIdentity = new object();
            m_generation = 0;
        }
    }

    /// <summary>Gets how many distinct keys are interned.</summary>
    public int Count => m_names.Count;
    /// <summary>Gets the deterministic storage charge for retained names and their table entries. Spare backing
    /// capacity is bounded separately by <see cref="StateCapacity.MaxCellKeys"/>.</summary>
    public long Bytes => m_bytes;
    /// <summary>Gets the interned names in mint order, indexed by <see cref="CellKey.Ordinal"/>.</summary>
    public IReadOnlyList<CellName> Names => m_readOnlyNames;

    /// <summary>Gets the name behind a key this table minted.</summary>
    /// <param name="key">The key to render.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="key"/> is invalid or from another table.</exception>
    public CellName this[CellKey key] => (TryGetName(
        key: key,
        name: out var name
    )
        ? name
        : throw new ArgumentOutOfRangeException(
            paramName: nameof(key),
            actualValue: key.Ordinal,
            message: "The cell key is invalid or was minted by another table."
        )
    );

    /// <summary>Interns a name, returning the key it already holds when the name is known.</summary>
    /// <param name="name">The cell key name.</param>
    /// <returns>The interned key.</returns>
    /// <exception cref="InvalidOperationException">The table already holds
    /// <see cref="StateCapacity.MaxCellKeys"/> distinct names.</exception>
    public CellKey Intern(CellName name) => (TryIntern(
        key: out var key,
        name: name,
        reason: out var reason
    )
        ? key
        : throw new InvalidOperationException(message: reason)
    );
    /// <summary>Attempts to intern a name, refusing by name at the table's ceiling rather than throwing.</summary>
    /// <param name="name">The cell key name.</param>
    /// <param name="key">The interned key on success; otherwise the invalid default.</param>
    /// <param name="reason">Why the name was refused, or empty on success.</param>
    /// <returns><see langword="true"/> when the name is interned.</returns>
    public bool TryIntern(CellName name, out CellKey key, out string reason) {
        if (m_byName.TryGetValue(
            key: name.Value,
            value: out key
        )) {
            reason = string.Empty;

            return true;
        }

        if (!TryAdmitIntern(name: name, reason: out reason)) {
            key = default;
            return false;
        }

        key = new CellKey(
            ordinal: m_names.Count,
            tableIdentity: ((m_source is null) ? m_identity : m_runtimeIdentity),
            generation: m_generation
        );

        m_byName.Add(
            key: name.Value,
            value: key
        );
        m_names.Add(item: name);
        m_keys.Add(item: key);
        var digest = NameDigest(name: name.Value);

        m_nameDigests.Add(item: digest);
        var integer = (CanonicalDecimal.TryParse(text: name.Value, value: out var spelled) ? spelled : NoInteger);

        m_integers.Add(item: integer);
        m_bytes += EntryBytes(name: name);
        m_ledger = m_ledger.Add(element: digest);
        NoteIndex(integer: integer, key: key);

        reason = string.Empty;

        return true;
    }
    /// <summary>Attempts to resolve the already-interned key whose name is an index's decimal spelling
    /// (<see cref="IndexKeyCache"/>), without minting one: the same answer as <see cref="TryResolve(ReadOnlySpan{char}, out CellKey)"/>
    /// over that spelling, read from an index table instead of a string lookup when the key is local.</summary>
    /// <param name="index">The index.</param>
    /// <param name="key">The existing key on success; otherwise the invalid default.</param>
    /// <returns>Whether this table or its compiler symbol source already knows the index's name.</returns>
    public bool TryResolveIndex(long index, out CellKey key) {
        if ((((ulong)index) < ((ulong)m_indexKeys.Length)) && m_indexKeys[index].IsValid) {
            key = m_indexKeys[index];

            return true;
        }

        return TryResolve(key: out key, name: IndexKeyCache.Get(index: index).AsSpan());
    }
    /// <summary>Attempts to read the integer a key's name spells in <see cref="CanonicalDecimal"/>, the spelling
    /// <see cref="IndexKeyCache"/> gives an index, without parsing it again: the inverse of
    /// <see cref="TryResolveIndex"/>, which names this key for exactly the integer this answers.</summary>
    /// <param name="key">The key to read.</param>
    /// <param name="integer">The integer on success; otherwise zero.</param>
    /// <returns><see langword="true"/> when the key addresses this table and its name is an integer's canonical
    /// spelling.</returns>
    public bool TryKeyInteger(CellKey key, out long integer) {
        if (!TryGetAddress(key: key, name: out var name, ordinal: out var ordinal)) {
            integer = 0L;
            return false;
        }
        if ((ordinal >= 0) && ((integer = m_integers[ordinal]) != NoInteger)) {
            return true;
        }
        return CanonicalDecimal.TryParse(text: name.Value, value: out integer);
    }
    /// <summary>Attempts to read the participant index a key's name spells: a <see cref="TryKeyInteger"/> answer
    /// that is non-negative and fits an <see cref="int"/>.</summary>
    /// <param name="key">The key to read.</param>
    /// <param name="index">The non-negative index on success; otherwise <c>-1</c>.</param>
    /// <returns><see langword="true"/> when the key addresses this table and its name spells such an index.</returns>
    public bool TryCandidateIndex(CellKey key, out int index) {
        var spelled = (TryKeyInteger(key: key, integer: out var integer) && (((ulong)integer) <= int.MaxValue));

        index = (spelled ? ((int)integer) : -1);

        return spelled;
    }
    /// <summary>Attempts to read the name behind a key this table minted.</summary>
    /// <param name="key">The key to render.</param>
    /// <param name="name">The name on success; otherwise the default.</param>
    /// <returns><see langword="true"/> when the key addresses this table.</returns>
    public bool TryGetName(CellKey key, out CellName name) {
        if (key.IsValid && (key.Ordinal < m_initialCount) && key.BelongsTo(tableIdentity: m_identity)) {
            name = m_names[key.Ordinal];
            return true;
        }
        if (
            key.IsValid &&
            (key.Ordinal < m_keys.Count) &&
            (m_keys[key.Ordinal] == key)
        ) {
            name = m_names[key.Ordinal];

            return true;
        }

        if (m_source is not null) {
            return m_source.TryGetName(key: key, name: out name);
        }

        name = default;

        return false;
    }
    /// <summary>Attempts to resolve an already-interned name without minting a new key.</summary>
    /// <param name="name">The cell key name.</param>
    /// <param name="key">The interned key on success; otherwise the invalid default.</param>
    /// <returns><see langword="true"/> when the name is already interned.</returns>
    public bool TryResolve(CellName name, out CellKey key) => (m_byName.TryGetValue(
        key: name.Value,
        value: out key
    ) || ((m_source is not null) && m_source.TryResolve(key: out key, name: name)));
    /// <summary>Resolves an existing name directly from characters without allocating a string or admitting a key.</summary>
    /// <param name="name">The cell key spelling.</param>
    /// <param name="key">The existing key on success; otherwise the invalid default.</param>
    /// <returns>Whether this table or its compiler symbol source already knows the name.</returns>
    public bool TryResolve(ReadOnlySpan<char> name, out CellKey key) => (
        m_byName.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(key: name, value: out key) ||
        ((m_source is not null) && m_source.TryResolve(key: out key, name: name))
    );

    internal CellKey KeyAt(int ordinal) => m_keys[ordinal];
    internal void AddNameTo(ref Fnv1aHash hash, int ordinal) {
        hash.Add(value: ((ordinal < 0) ? AbsentNameDigest : m_nameDigests[ordinal]));
    }

    private static ulong NameDigest(string? name) {
        var hash = Fnv1aHash.Create();

        hash.Add(value: ((name is null) ? 0UL : 1UL));
        if (name is not null) {
            hash.Add(value: ((ulong)name.Length));
            hash.Add(value: Fnv1aHash.Compute(values: name.AsSpan()));
        }
        return hash.Value;
    }

    internal void AddLedgerTo(ref Fnv1aHash hash) {
        // A multiset digest makes the resource identity independent of allocation order. Unlike XOR,
        // repeated contributions cannot cancel in pairs. Mint/release update only the affected name digest.
        hash.Add(value: ((ulong)m_names.Count));
        hash.Add(value: m_ledger.Value);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryGetAddress(CellKey key, out CellName name, out int ordinal) {
        ordinal = key.Ordinal;
        if ((((uint)ordinal) < ((uint)m_initialCount)) && key.BelongsTo(tableIdentity: m_identity)) {
            name = m_names[ordinal];
            return true;
        }
        return TryGetRuntimeAddress(key: key, name: out name, ordinal: out ordinal);
    }

    private bool TryGetRuntimeAddress(CellKey key, out CellName name, out int ordinal) {
        ordinal = key.Ordinal;
        if (key.IsValid && (ordinal < m_keys.Count) && (m_keys[ordinal] == key)) {
            name = m_names[ordinal];
            return true;
        }
        if ((m_source is not null) && m_source.TryGetName(key: key, name: out name)) {
            // A compiler may bind an additional literal after the arena was constructed. Resolve that symbol
            // against existing local membership without interning on a read or sharing runtime mutation.
            ordinal = (m_byName.TryGetValue(key: name.Value, value: out var local) ? local.Ordinal : -1);
            return true;
        }
        name = default;
        ordinal = -1;
        return false;
    }
}
