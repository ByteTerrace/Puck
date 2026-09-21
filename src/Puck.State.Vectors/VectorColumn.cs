namespace Puck.State;

/// <summary>
/// A typed view over one row's vector column: the row's dimension count proved once, and every read and write
/// against that row typed by it.
/// </summary>
/// <remarks>
/// <para><see cref="CellValue.Vector"/> carries components as an opaque payload and knows nothing about the row
/// they belong to. This view is the typed half beside it: <see cref="TryOpen"/> proves the row is a stored vector
/// row and remembers <see cref="Dimensions"/>, so a caller walking a column never re-asks what kind of row it is
/// reading.</para>
/// <para>A span a read answers with aliases the arena's own storage, so consume it before the next write, rewind,
/// or relayout. The view holds no snapshot: it reads the arena live, and a relayout invalidates it because the
/// row's dimensions and ordinals are reassigned.</para>
/// </remarks>
public readonly struct VectorColumn : IEquatable<VectorColumn> {
    private readonly StateArena? m_arena;
    private readonly int m_dimensions;
    private readonly int m_rowOrdinal;
    private readonly RowShape m_shape;

    private VectorColumn(StateArena arena, int rowOrdinal, int dimensions, RowShape shape) {
        m_arena = arena;
        m_dimensions = dimensions;
        m_rowOrdinal = rowOrdinal;
        m_shape = shape;
    }

    /// <summary>Gets the arena this view reads and writes.</summary>
    /// <exception cref="InvalidOperationException">The view was never opened.</exception>
    public StateArena Arena => (m_arena ?? throw new InvalidOperationException(message: "A default VectorColumn views no row; open one with TryOpen."));
    /// <summary>Gets how many cells the column currently holds.</summary>
    public int Count => (m_arena?.CellCount(rowOrdinal: m_rowOrdinal) ?? 0);
    /// <summary>Gets how many components every cell of the column stores.</summary>
    public int Dimensions => m_dimensions;
    /// <summary>Gets a value indicating whether this view was opened over a row.</summary>
    public bool IsOpen => (m_arena is not null);
    /// <summary>Gets the viewed row's catalog ordinal.</summary>
    public int RowOrdinal => m_rowOrdinal;
    /// <summary>Gets the viewed row's storage shape.</summary>
    public RowShape Shape => m_shape;

    /// <summary>Opens a typed view over one row's vector column.</summary>
    /// <param name="arena">The arena holding the row.</param>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="column">The view on success; otherwise the default.</param>
    /// <param name="refusal">Why the row was refused, or the default on success.</param>
    /// <returns><see langword="true"/> when the row is a stored vector row this arena lays out.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="arena"/> is <see langword="null"/>.</exception>
    public static bool TryOpen(StateArena arena, int rowOrdinal, out VectorColumn column, out VectorTransformRefusal refusal) {
        ArgumentNullException.ThrowIfNull(argument: arena);

        column = default;

        if (((uint)rowOrdinal) >= ((uint)arena.Layout.RowCount)) {
            refusal = new VectorTransformRefusal(
                Code: RuleRefusal.StateRowUnknown,
                Reason: $"row ordinal {rowOrdinal} names no row of this arena"
            );

            return false;
        }

        ref readonly var layout = ref arena.Layout[rowOrdinal];
        var name = arena.Catalog.Descriptors[rowOrdinal].Name;

        if (layout.HostOwned || !layout.IsStored) {
            refusal = new VectorTransformRefusal(
                Code: RuleRefusal.StateRowUnknown,
                Reason: $"row '{name}' stores no cells of its own"
            );

            return false;
        }
        if (layout.Kind != CellKind.Vector) {
            refusal = new VectorTransformRefusal(
                Code: RuleRefusal.VectorOperandNotVector,
                Reason: $"row '{name}' stores {layout.Kind}, not Vector"
            );

            return false;
        }

        column = new VectorColumn(
            arena: arena,
            dimensions: layout.Dimensions,
            rowOrdinal: rowOrdinal,
            shape: layout.Shape
        );
        refusal = default;

        return true;
    }

    /// <summary>Determines whether two views read the same row of the same arena.</summary>
    /// <param name="left">The left view.</param>
    /// <param name="right">The right view.</param>
    /// <returns><see langword="true"/> when the two are equal.</returns>
    public static bool operator ==(VectorColumn left, VectorColumn right) => left.Equals(other: right);
    /// <summary>Determines whether two views read different rows or different arenas.</summary>
    /// <param name="left">The left view.</param>
    /// <param name="right">The right view.</param>
    /// <returns><see langword="true"/> when the two differ.</returns>
    public static bool operator !=(VectorColumn left, VectorColumn right) => !left.Equals(other: right);

    /// <inheritdoc/>
    public bool Equals(VectorColumn other) => (
        ReferenceEquals(
        objA: m_arena,
        objB: other.m_arena
    ) &&
        (m_rowOrdinal == other.m_rowOrdinal)
    );
    /// <inheritdoc/>
    public override bool Equals(object? obj) => ((obj is VectorColumn other) && Equals(other: other));
    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(
        value1: m_arena,
        value2: m_rowOrdinal
    );
    /// <summary>Returns the name behind one of the column's keys, for a message naming what a caller addressed.</summary>
    /// <param name="key">The cell key.</param>
    /// <returns>The interned name, or the key's ordinal in angle brackets when the key belongs to no arena this
    /// view can read.</returns>
    public string KeyName(CellKey key) => ((
        (m_arena is { } arena) &&
        arena.Keys.TryGetName(
        key: key,
        name: out var name
    )
    )
        ? name.Value
        : $"<key {key.Ordinal}>"
    );
    /// <summary>Returns the viewed row's authored name.</summary>
    /// <returns>The row name.</returns>
    public string RowName() => Arena.Catalog.Descriptors[m_rowOrdinal].Name;
    /// <summary>Attempts to read one cell's components as a borrowed span into the arena's storage.</summary>
    /// <param name="key">The cell key, interned by the arena's catalog.</param>
    /// <param name="components">The cell's components on success; otherwise empty.</param>
    /// <returns><see langword="true"/> when the column holds the cell.</returns>
    public bool TryRead(CellKey key, out ReadOnlySpan<sbyte> components) {
        if (m_arena is { } arena) {
            return arena.TryReadVector(
                components: out components,
                key: key,
                rowOrdinal: m_rowOrdinal
            );
        }

        components = default;

        return false;
    }
    /// <summary>Attempts to read one cell's components as the opaque payload a kernel consumes.</summary>
    /// <param name="key">The cell key, interned by the arena's catalog.</param>
    /// <param name="components">The cell's components on success; otherwise empty.</param>
    /// <returns><see langword="true"/> when the column holds the cell.</returns>
    /// <remarks>The memory aliases the arena's storage exactly as <see cref="TryRead"/>'s span does; it exists so
    /// a caller gathering many cells for one kernel call does not copy each of them.</remarks>
    public bool TryReadMemory(CellKey key, out ReadOnlyMemory<sbyte> components) {
        if (
            (m_arena is { } arena) &&
            arena.TryRead(
            key: key,
            rowOrdinal: m_rowOrdinal,
            value: out var value
        ) &&
            value.HasValue &&
            (value.Kind == CellKind.Vector)
        ) {
            components = value.AsVector;

            return true;
        }

        components = default;

        return false;
    }
    /// <summary>Attempts to write one cell's components.</summary>
    /// <param name="key">The cell key, interned by the arena's catalog.</param>
    /// <param name="components">The components to store; their count must be <see cref="Dimensions"/>.</param>
    /// <param name="reason">Why the write was refused, or empty on success.</param>
    /// <returns><see langword="true"/> when the write was admitted and stored.</returns>
    public bool TryWrite(CellKey key, ReadOnlySpan<sbyte> components, out string reason) {
        if (m_arena is { } arena) {
            return arena.TryWriteVector(
                components: components,
                key: key,
                reason: out reason,
                rowOrdinal: m_rowOrdinal
            );
        }

        reason = "a default VectorColumn views no row";

        return false;
    }
}
