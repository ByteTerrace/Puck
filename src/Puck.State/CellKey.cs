using System.Collections.ObjectModel;

namespace Puck.State;

/// <summary>A cell key interned by one <see cref="CellKeyTable"/> — the runtime address a compiled read or write
/// carries in place of the authored string. Row names keep <see cref="CellName"/>; only keys intern.</summary>
/// <remarks>A key is bound to the table that minted it, so a key from one catalog never resolves against another.
/// The default value is invalid.</remarks>
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
/// The per-catalog intern table for cell keys: authored keys are interned in declaration order while the catalog
/// compiles, and a key a write mints later appends in mint order. Both orders are a function of the document and
/// the input sequence alone, so two runs of the same document and input intern the same names at the same
/// ordinals.
/// </summary>
/// <remarks>Interning is global to the table, not per row: one name used by many rows occupies one ordinal. The
/// table is not synchronized: the catalog compiles it once, and a runtime mint appends only on the thread that
/// steps the owning world, which is what keeps the mint order a function of the input sequence.</remarks>
public sealed class CellKeyTable {
    private readonly Dictionary<string, CellKey> m_byName;
    private readonly object m_identity;
    private readonly List<CellName> m_names;
    private readonly ReadOnlyCollection<CellName> m_readOnlyNames;

    /// <summary>Initializes an empty table.</summary>
    public CellKeyTable() {
        m_byName = new Dictionary<string, CellKey>(comparer: StringComparer.Ordinal);
        m_identity = new object();
        m_names = [];
        m_readOnlyNames = m_names.AsReadOnly();
    }

    /// <summary>Gets how many distinct keys are interned.</summary>
    public int Count => m_names.Count;
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

        if (m_names.Count >= StateCapacity.MaxCellKeys) {
            key = default;
            reason = $"Cell key '{name.Value}' cannot be interned: the catalog already holds {StateCapacity.MaxCellKeys} distinct keys.";

            return false;
        }

        key = new CellKey(
            ordinal: m_names.Count,
            tableIdentity: m_identity
        );

        m_byName.Add(
            key: name.Value,
            value: key
        );
        m_names.Add(item: name);

        reason = string.Empty;

        return true;
    }
    /// <summary>Attempts to read the name behind a key this table minted.</summary>
    /// <param name="key">The key to render.</param>
    /// <param name="name">The name on success; otherwise the default.</param>
    /// <returns><see langword="true"/> when the key addresses this table.</returns>
    public bool TryGetName(CellKey key, out CellName name) {
        if (
            key.IsValid &&
            key.BelongsTo(tableIdentity: m_identity) &&
            (key.Ordinal < m_names.Count)
        ) {
            name = m_names[key.Ordinal];

            return true;
        }

        name = default;

        return false;
    }
    /// <summary>Attempts to resolve an already-interned name without minting a new key.</summary>
    /// <param name="name">The cell key name.</param>
    /// <param name="key">The interned key on success; otherwise the invalid default.</param>
    /// <returns><see langword="true"/> when the name is already interned.</returns>
    public bool TryResolve(CellName name, out CellKey key) => m_byName.TryGetValue(
        key: name.Value,
        value: out key
    );
}
