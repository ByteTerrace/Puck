namespace Puck.State;

/// <summary>An interned static address for a (row handle, key) pair, resolved per layout.</summary>
/// <param name="Ordinal">The descriptor's ordinal in the compiled cell table, or -1 for invalid.</param>
public readonly record struct StateCellHandle(int Ordinal) {
    /// <summary>Gets whether this handle points to a valid entry.</summary>
    public bool IsValid => (Ordinal >= 0);
    /// <summary>The default invalid handle.</summary>
    public static readonly StateCellHandle Invalid = new(-1);
}

/// <summary>Describes an interned static cell address: its row handle and cell key.</summary>
/// <param name="RowHandle">The compiled state row handle.</param>
/// <param name="Key">The cell key name.</param>
public readonly record struct StateCellDescriptor(StateHandle RowHandle, CellName Key);

/// <summary>An immutable table of interned static cell addresses compiled for a program.</summary>
public sealed class StateCellTable {
    private readonly StateCellDescriptor[] m_descriptors;
    private readonly Dictionary<(int RowOrdinal, CellName Key), StateCellHandle> m_byKey;

    /// <summary>Gets an empty static cell table.</summary>
    public static readonly StateCellTable Empty = new([], []);

    internal StateCellTable(StateCellDescriptor[] descriptors, Dictionary<(int RowOrdinal, CellName Key), StateCellHandle> byKey) {
        m_descriptors = descriptors;
        m_byKey = byKey;
    }

    /// <summary>Gets the count of interned static cell addresses.</summary>
    public int Count => m_descriptors.Length;

    /// <summary>Gets the descriptor for the given handle.</summary>
    public StateCellDescriptor this[StateCellHandle handle] =>
        ((handle.Ordinal >= 0 && handle.Ordinal < m_descriptors.Length)
            ? m_descriptors[handle.Ordinal]
            : default);

    /// <summary>Attempts to resolve a static cell handle for the given row handle and key.</summary>
    public bool TryResolve(StateHandle rowHandle, CellName key, out StateCellHandle handle) {
        if (
            rowHandle.IsValid &&
            m_byKey.TryGetValue((rowHandle.Ordinal, key), out var found) &&
            (m_descriptors[found.Ordinal].RowHandle == rowHandle)
        ) {
            handle = found;
            return true;
        }

        handle = StateCellHandle.Invalid;
        return false;
    }
}

/// <summary>Builds an interned table of static cell addresses during rule compilation.</summary>
public sealed class StateCellTableBuilder {
    private readonly List<StateCellDescriptor> m_descriptors = [];
    private readonly Dictionary<(int RowOrdinal, CellName Key), StateCellHandle> m_byKey = [];

    /// <summary>Interns a (row handle, key) pair and returns its static cell handle.</summary>
    public StateCellHandle Intern(StateHandle rowHandle, CellName key) {
        if (!rowHandle.IsValid) {
            return StateCellHandle.Invalid;
        }

        var lookupKey = (rowHandle.Ordinal, key);
        if (m_byKey.TryGetValue(lookupKey, out var existing)) {
            return existing;
        }

        var ordinal = m_descriptors.Count;
        var handle = new StateCellHandle(ordinal);
        m_descriptors.Add(new StateCellDescriptor(rowHandle, key));
        m_byKey[lookupKey] = handle;
        return handle;
    }

    /// <summary>Builds an immutable static cell table.</summary>
    public StateCellTable Build() =>
        ((m_descriptors.Count == 0)
            ? StateCellTable.Empty
            : new StateCellTable([.. m_descriptors], new(m_byKey)));
}
