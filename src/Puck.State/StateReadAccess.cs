namespace Puck.State;

/// <summary>A failed acquisition. Restricted access retains the fault independently of exception handling and
/// evaluator scratch. The message contains no row, key or ordinal.</summary>
public sealed class StateUnavailableException : InvalidOperationException {
    internal StateUnavailableException() : base("State is unavailable to this preparation.") { }
}

/// <summary>Read operations over one store. Restricted access checks whole-row policy before returning any
/// shape, value, membership or ordering information; it never exposes the backing arena or its row objects.</summary>
public sealed class StateReadAccess {
    private readonly StateArena m_arena;
    private readonly Func<int, bool>? m_admits;
    private readonly ArenaScratch m_scratch;
    private readonly List<int>? m_rows;
    private readonly CellKeyTable? m_keys;
    private readonly StateCatalog? m_catalog;

    internal StateReadAccess(StateArena arena, Func<int, bool>? admits) {
        m_arena = arena;
        m_admits = admits;
        m_scratch = admits is null ? arena.Scratch : new ArenaScratch();
        m_rows = admits is null ? null : [];
        m_keys = admits is null ? null : new CellKeyTable();
        m_catalog = admits is null ? null : arena.Catalog;
    }

    /// <summary>Whether any acquisition has failed. It is never reset during this access's lifetime.</summary>
    public bool Failed { get; private set; }
    /// <summary>Evaluation scratch, private to restricted preparation.</summary>
    public ArenaScratch Scratch => m_scratch;

    /// <summary>Uses the same read kernels for an authority that already owns its store.</summary>
    public static implicit operator StateReadAccess(StateArena arena) => arena.ReadAccess;

    private int Require(int row) {
        var actual = m_rows is null ? row : ((uint)row < (uint)m_rows.Count ? m_rows[row] : -1);
        RequireSource(actual);
        return actual;
    }
    private void RequireSource(int row) {
        if (Failed || (m_catalog is not null && !ReferenceEquals(m_catalog, m_arena.Catalog)) ||
            (uint)row >= (uint)m_arena.Catalog.Count || m_admits?.Invoke(row) == false) { Unavailable(); }
    }
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private void Unavailable() {
        // Authority access is a reusable adapter, not a transaction. Restricted access is always fresh.
        Failed = m_admits is not null;
        throw new StateUnavailableException();
    }

    /// <summary>Acquires a named row. Missing and withheld names fail in the same way.</summary>
    public int ResolveRow(string name) {
        if (!m_arena.Catalog.TryResolve(StateLane.Document, name, out var handle)) {
            Unavailable();
        }
        RequireSource(handle.Ordinal);
        if (m_rows is null) { return handle.Ordinal; }
        var local = m_rows.IndexOf(handle.Ordinal);
        if (local >= 0) { return local; }
        m_rows.Add(handle.Ordinal);
        return m_rows.Count - 1;
    }
    /// <summary>Resolves an authority declaration, or acquires a restricted declaration. A restricted negative
    /// lookup is unavailable, never an ordinary missing-row result.</summary>
    public bool TryResolveRow(string name, out int rowOrdinal) {
        if (m_admits is not null) { rowOrdinal = ResolveRow(name); return true; }
        if (m_arena.Catalog.TryResolve(StateLane.Document, name, out var handle)) {
            rowOrdinal = handle.Ordinal;
            return true;
        }
        rowOrdinal = -1;
        return false;
    }
    /// <summary>Acquires a declaration without retaining a row's cells, seeds or storage metadata.</summary>
    public StateRowDeclaration Declaration(int rowOrdinal) {
        return StateRowDeclaration.From(m_arena.Rows[Require(rowOrdinal)]);
    }
    /// <summary>Reads membership count after whole-row admission.</summary>
    public int CellCount(int rowOrdinal) => m_arena.CellCount(Require(rowOrdinal));
    /// <summary>Reads a value after whole-row admission.</summary>
    public bool TryRead(int rowOrdinal, CellKey key, out CellValue value) {
        var row = Require(rowOrdinal);
        value = default;
        if (!TrySourceKey(key, out var source) || !m_arena.TryRead(row, source, out value)) { return false; }
        value = Detach(value);
        return true;
    }
    /// <summary>Reads an effective value after whole-row admission.</summary>
    public bool TryReadLive(int rowOrdinal, CellKey key, in ArenaTime time, out CellValue value) {
        var row = Require(rowOrdinal);
        value = default;
        if (!TrySourceKey(key, out var source) || !m_arena.TryReadLive(row, source, in time, out value)) { return false; }
        value = Detach(value);
        return true;
    }
    /// <summary>Reads an effective numeric value after whole-row admission.</summary>
    public bool TryReadLiveNumber(int rowOrdinal, CellKey key, in ArenaTime time, out long value) {
        var row = Require(rowOrdinal);
        value = default;
        return TrySourceKey(key, out var source) && m_arena.TryReadLiveNumber(row, source, in time, out value);
    }
    /// <summary>Walks admitted membership in its current order.</summary>
    public bool TryNextCell(int rowOrdinal, ref int cursor, out CellKey key) {
        var row = Require(rowOrdinal);
        key = default;
        if (!m_arena.TryNextCell(row, ref cursor, out var source)) { return false; }
        key = LocalKey(source);
        return true;
    }
    /// <summary>Reads one admitted member's key by position.</summary>
    public bool TryKeyAt(int rowOrdinal, int position, out CellKey key) {
        key = default;
        if (!m_arena.TryKeyAt(Require(rowOrdinal), position, out var source)) { return false; }
        key = LocalKey(source);
        return true;
    }
    /// <summary>Resolves a named key only within an admitted row.</summary>
    public bool TryResolveKey(int rowOrdinal, CellName name, out CellKey key) {
        var row = Require(rowOrdinal);
        if (m_arena.Keys.TryResolve(name, out var source) && m_arena.TryRead(row, source, out _)) {
            key = LocalKey(source);
            return true;
        }
        key = default;
        return false;
    }
    /// <summary>Reads a member's name only within an admitted row.</summary>
    public bool TryKeyName(int rowOrdinal, CellKey key, out CellName name) {
        var row = Require(rowOrdinal);
        if (TrySourceKey(key, out var source) && m_arena.TryRead(row, source, out _) && m_arena.Keys.TryGetName(source, out name)) { return true; }
        name = default;
        return false;
    }

    private CellKey LocalKey(CellKey source) => m_keys is null ? source : m_keys.Intern(m_arena.Keys[source]);
    private bool TrySourceKey(CellKey key, out CellKey source) {
        if (m_keys is null) { source = key; return true; }
        source = default;
        return m_keys.TryGetName(key, out var name) && m_arena.Keys.TryResolve(name, out source);
    }
    private CellValue Detach(CellValue value) => m_keys is not null && value.Kind == CellKind.Vector
        ? CellValue.Vector(value.AsVector.ToArray()) : value;
}
