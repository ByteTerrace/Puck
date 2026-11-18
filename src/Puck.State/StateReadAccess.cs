namespace Puck.State;

/// <summary>A failed acquisition. Restricted access retains the fault independently of exception handling and
/// evaluator scratch. The message contains no row, key or ordinal.</summary>
public sealed class StateUnavailableException : InvalidOperationException {
    internal StateUnavailableException() : base("State is unavailable to this preparation.") { }
}

/// <summary>A row acquired by one read access. It exposes no catalog or local ordinal;
/// the default value and a handle minted by another access are unavailable.</summary>
public readonly struct StateReadRow {
    internal StateReadRow(StateReadAccess owner, int ordinal) { Owner = owner; Ordinal = ordinal; }
    internal StateReadAccess? Owner { get; }
    internal int Ordinal { get; }
}

/// <summary>Read operations over one store. Restricted access checks whole-row policy before returning any
/// shape, value, membership or ordering information; it never exposes the backing arena or its row objects.</summary>
public sealed class StateReadAccess {
    private readonly StateArena m_arena;
    private readonly Func<int, bool>? m_admits;
    private readonly ArenaScratch m_scratch;
    private readonly CellKeyTable? m_keys;
    private readonly StateCatalog? m_catalog;

    internal StateReadAccess(StateArena arena, Func<int, bool>? admits) {
        m_arena = arena;
        m_admits = admits;
        m_scratch = admits is null ? arena.Scratch : new ArenaScratch();
        m_keys = admits is null ? null : new CellKeyTable();
        m_catalog = admits is null ? null : arena.Catalog;
    }

    /// <summary>Whether any acquisition has failed. It is never reset during this access's lifetime.</summary>
    public bool Failed { get; private set; }
    /// <summary>Evaluation scratch, private to restricted preparation.</summary>
    public ArenaScratch Scratch => m_scratch;

    private int Require(StateReadRow row) {
        if (!ReferenceEquals(row.Owner, this)) { Unavailable(); }
        RequireSource(row.Ordinal);
        return row.Ordinal;
    }
    // Catalog ordinals enter only through the explicit authority overloads of the shared kernels.
    internal StateReadRow AuthorityRow(int ordinal) {
        if (m_admits is not null) { Unavailable(); }
        RequireSource(ordinal);
        return new(this, ordinal);
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
    public StateReadRow ResolveRow(string name) {
        if (!m_arena.Catalog.TryResolve(StateLane.Document, name, out var handle)) {
            Unavailable();
        }
        RequireSource(handle.Ordinal);
        return new(this, handle.Ordinal);
    }
    /// <summary>Resolves an authority declaration, or acquires a restricted declaration. A restricted negative
    /// lookup is unavailable, never an ordinary missing-row result.</summary>
    public bool TryResolveRow(string name, out StateReadRow rowOrdinal) {
        if (m_admits is not null) { rowOrdinal = ResolveRow(name); return true; }
        if (m_arena.Catalog.TryResolve(StateLane.Document, name, out var handle)) {
            rowOrdinal = new(this, handle.Ordinal);
            return true;
        }
        rowOrdinal = default;
        return false;
    }
    /// <summary>Acquires a declaration without retaining a row's cells, seeds or storage metadata.</summary>
    public StateRowDeclaration Declaration(StateReadRow rowOrdinal) {
        var declaration = StateRowDeclaration.From(m_arena.Rows[Require(rowOrdinal)]);
        if (m_admits is null) { return declaration; }
        // A detached descriptor is still an observation. Acquire every row name it carries before returning it.
        RequireNamed(declaration.EffectiveDomain is StateDomain.KeysOf keys ? keys.Row.Value : null);
        RequireNamed(declaration.Visibility?.ReadersFrom);
        RequireNamed(declaration.Inverse?.Tokens.Value);
        RequireNamed(declaration.Inverse?.Codes.Value);
        RequireNamed(declaration.Knowledge?.Source);
        RequireNamed(declaration.Knowledge?.Mask);
        RequireNamed(declaration.Knowledge?.Positions);
        RequireNamed(declaration.PhaseOf);
        // This access admits state rows, not generator programs or other sections. Their symbolic names and
        // inline programs need section entitlement and are not part of a row-only acquisition.
        if (declaration.Dynamics is not null || declaration.ValuesFrom is not null || declaration.Space is not null ||
            declaration.Enum is not null || declaration.Domain is StateDomain.CellsOf) { Unavailable(); }
        return declaration.Draw is { } draw
            ? declaration with { Draw = draw with { Source = null, Generator = null } }
            : declaration;
    }
    internal StateDomain Domain(StateReadRow row) => m_arena.Rows[Require(row)].EffectiveDomain;
    private void RequireNamed(string? name) {
        if (name is not null) { _ = ResolveRow(name); }
    }
    /// <summary>Reads membership count after whole-row admission.</summary>
    public int CellCount(StateReadRow rowOrdinal) => m_arena.CellCount(Require(rowOrdinal));
    /// <summary>Reads a value after whole-row admission.</summary>
    public bool TryRead(StateReadRow rowOrdinal, CellKey key, out CellValue value) {
        var row = Require(rowOrdinal);
        value = default;
        if (!TrySourceKey(key, out var source) || !m_arena.TryRead(row, source, out value)) { return false; }
        value = Detach(value);
        return true;
    }
    /// <summary>Reads an effective value after whole-row admission.</summary>
    public bool TryReadLive(StateReadRow rowOrdinal, CellKey key, in ArenaTime time, out CellValue value) {
        var row = Require(rowOrdinal);
        value = default;
        if (!TrySourceKey(key, out var source) || !m_arena.TryReadLive(row, source, in time, out value)) { return false; }
        value = Detach(value);
        return true;
    }
    /// <summary>Reads an effective numeric value after whole-row admission.</summary>
    public bool TryReadLiveNumber(StateReadRow rowOrdinal, CellKey key, in ArenaTime time, out long value) {
        var row = Require(rowOrdinal);
        value = default;
        return TrySourceKey(key, out var source) && m_arena.TryReadLiveNumber(row, source, in time, out value);
    }
    /// <summary>Walks admitted membership in its current order.</summary>
    public bool TryNextCell(StateReadRow rowOrdinal, ref int cursor, out CellKey key) {
        var row = Require(rowOrdinal);
        key = default;
        if (!m_arena.TryNextCell(row, ref cursor, out var source)) { return false; }
        key = LocalKey(source);
        return true;
    }
    /// <summary>Reads one admitted member's key by position.</summary>
    public bool TryKeyAt(StateReadRow rowOrdinal, int position, out CellKey key) {
        key = default;
        if (!m_arena.TryKeyAt(Require(rowOrdinal), position, out var source)) { return false; }
        key = LocalKey(source);
        return true;
    }
    /// <summary>Resolves a named key only within an admitted row.</summary>
    public bool TryResolveKey(StateReadRow rowOrdinal, CellName name, out CellKey key) {
        var row = Require(rowOrdinal);
        if (m_arena.Keys.TryResolve(name, out var source) && m_arena.TryRead(row, source, out _)) {
            key = LocalKey(source);
            return true;
        }
        key = default;
        return false;
    }
    /// <summary>Reads a member's name only within an admitted row.</summary>
    public bool TryKeyName(StateReadRow rowOrdinal, CellKey key, out CellName name) {
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
    private CellValue Detach(CellValue value) => m_keys is null ? value : value.DetachedCopy();
}
