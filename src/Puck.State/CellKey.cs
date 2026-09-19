using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using Puck.Maths;

namespace Puck.State;

/// <summary>A cell key interned by one <see cref="CellKeyTable"/> — the runtime address a compiled read or write
/// carries in place of the authored string. Row names keep <see cref="CellName"/>; only keys intern.</summary>
/// <remarks>Compiled keys also resolve in arenas built from their catalog. Runtime keys belong to one arena and
/// cease to resolve when their mint is rewound. The default value is invalid.</remarks>
public readonly record struct CellKey {
    private readonly int m_encodedOrdinal;
    private readonly object? m_tableIdentity;

    internal CellKey(int ordinal, object tableIdentity) {
        m_encodedOrdinal = checked((ordinal + 1));
        m_tableIdentity = tableIdentity;
    }

    /// <summary>Gets a value indicating whether this key was minted by a key table.</summary>
    public bool IsValid => (m_encodedOrdinal > 0);
    /// <summary>Gets the key's stable intern ordinal, or <c>-1</c> for the default invalid key.</summary>
    public int Ordinal => (m_encodedOrdinal - 1);

    internal bool BelongsTo(object tableIdentity) => ReferenceEquals(
        objA: m_tableIdentity,
        objB: tableIdentity
    );
}
/// <summary>
/// An intern table for compiled symbols or one arena's runtime keys. An arena starts with the catalog's keys,
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

    private object? m_epochIdentity;

    private readonly List<ulong> m_nameDigests;

    private Func<long>? m_byteBudget;
    private long m_bytes;
    private string[] m_hashNames = [];
    private ulong m_ledgerHash;
    private bool m_ledgerHashValid;

    /// <summary>Initializes an empty table.</summary>
    public CellKeyTable() {
        m_byName = new Dictionary<string, CellKey>(comparer: StringComparer.Ordinal);
        m_identity = new object();
        m_names = [];
        m_keys = [];
        m_nameDigests = [];
        m_readOnlyNames = m_names.AsReadOnly();
    }

    private CellKeyTable(CellKeyTable source) {
        m_source = source;
        m_identity = source.m_identity;
        m_names = new List<CellName>(collection: source.m_names);
        m_keys = new List<CellKey>(collection: source.m_keys);
        m_nameDigests = new List<ulong>(collection: source.m_nameDigests);
        m_bytes = source.m_bytes;
        m_byName = new Dictionary<string, CellKey>(dictionary: source.m_byName, comparer: StringComparer.Ordinal);
        m_readOnlyNames = m_names.AsReadOnly();
        m_initialCount = m_names.Count;
    }

    internal CellKeyTable Fork() => new(source: this);
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
            m_byName.Remove(key: m_names[index].Value);
        }
        m_nameDigests.RemoveRange(index: count, count: (m_nameDigests.Count - count));
        m_keys.RemoveRange(index: count, count: (m_keys.Count - count));
        m_names.RemoveRange(index: count, count: (m_names.Count - count));
        // Allocate a fresh identity only if a subsequent mint actually needs one. Ordinary candidate rewinds
        // allocate nothing, and a retained speculative handle cannot alias a later reuse of its ordinal.
        m_epochIdentity = null;
        m_ledgerHashValid = false;
        Array.Clear(array: m_hashNames);
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
            tableIdentity: ((m_source is null) ? m_identity : (m_epochIdentity ??= new object()))
        );

        m_byName.Add(
            key: name.Value,
            value: key
        );
        m_names.Add(item: name);
        m_keys.Add(item: key);
        m_nameDigests.Add(item: NameDigest(name: name.Value));
        m_bytes += EntryBytes(name: name);
        m_ledgerHashValid = false;

        reason = string.Empty;

        return true;
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
        if (!m_ledgerHashValid) {
            if (m_hashNames.Length < m_names.Count) {
                Array.Resize(array: ref m_hashNames, newSize: m_names.Count);
            }
            for (var index = 0; (index < m_names.Count); index++) {
                m_hashNames[index] = m_names[index].Value;
            }
            Array.Sort(array: m_hashNames, index: 0, length: m_names.Count, comparer: StringComparer.Ordinal);
            var ledger = Fnv1aHash.Create();

            ledger.Add(value: ((ulong)m_names.Count));
            for (var index = 0; (index < m_names.Count); index++) {
                var name = m_hashNames[index];

                ledger.Add(value: ((ulong)name.Length));
                ledger.Add(value: Fnv1aHash.Compute(values: name.AsSpan()));
            }
            m_ledgerHash = ledger.Value;
            m_ledgerHashValid = true;
            Array.Clear(array: m_hashNames);
        }
        hash.Add(value: m_ledgerHash);
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
