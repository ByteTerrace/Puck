namespace Puck.State;

/// <summary>One stored vector cell's address: the row's catalog ordinal and the cell key interned by that
/// catalog.</summary>
/// <param name="RowOrdinal">The row's catalog ordinal.</param>
/// <param name="Key">The cell key, interned by the arena's catalog.</param>
public readonly record struct VectorCellAddress(int RowOrdinal, CellKey Key);
/// <summary>
/// Where a vector transform's operand comes from, as a closed union over two cases: a cell the arena stores, or
/// components the caller carries.
/// </summary>
/// <remarks>Storage is inline — one address and one <see cref="ReadOnlyMemory{T}"/> beside a discriminator byte —
/// so a mix's eight terms cost no allocation. The default carrier holds no case at all:
/// <see cref="HasValue"/> is <see langword="false"/> and every accessor throws.</remarks>
[Union]
public readonly struct VectorSource : IEquatable<VectorSource>, IUnion {
    // The discriminator is stored so that the default carrier — every field zero — is the one value carrying no
    // case, without spending a second field on the question.
    private const byte CellCase = 1;
    private const byte LiteralCase = 2;

    private readonly byte m_discriminator;
    private readonly VectorCellAddress m_address;
    private readonly ReadOnlyMemory<sbyte> m_components;

    private VectorSource(byte discriminator, VectorCellAddress address, ReadOnlyMemory<sbyte> components) {
        m_address = address;
        m_components = components;
        m_discriminator = discriminator;
    }

    /// <summary>Gets the addressed cell of the <see cref="Cell"/> case.</summary>
    /// <exception cref="InvalidOperationException">The carrier holds another case, or none.</exception>
    public VectorCellAddress Address => (IsCell
        ? m_address
        : throw Mismatch(expected: nameof(Cell))
    );
    /// <summary>Gets the components of the <see cref="Literal"/> case.</summary>
    /// <exception cref="InvalidOperationException">The carrier holds another case, or none.</exception>
    public ReadOnlyMemory<sbyte> Components => (IsLiteral
        ? m_components
        : throw Mismatch(expected: nameof(Literal))
    );
    /// <summary>Gets a value indicating whether this carrier holds a case at all.</summary>
    public bool HasValue => (m_discriminator != 0);
    /// <summary>Gets a value indicating whether this carrier addresses a stored cell.</summary>
    public bool IsCell => (m_discriminator == CellCase);
    /// <summary>Gets a value indicating whether this carrier holds components of its own.</summary>
    public bool IsLiteral => (m_discriminator == LiteralCase);

    object? IUnion.Value => (m_discriminator switch {
        CellCase => m_address,
        LiteralCase => m_components,
        _ => null,
    });

    /// <summary>Creates the case addressing a vector cell the arena stores.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="key">The cell key, interned by the arena's catalog.</param>
    /// <returns>The carrier.</returns>
    public static VectorSource Cell(int rowOrdinal, CellKey key) => new(
        address: new VectorCellAddress(
            Key: key,
            RowOrdinal: rowOrdinal
        ),
        components: default,
        discriminator: CellCase
    );
    /// <summary>Creates the case carrying components of its own.</summary>
    /// <param name="components">The signed 8-bit components. The carrier keeps the memory as handed to it and
    /// never copies it, so the caller owns the lifetime of what it points at.</param>
    /// <returns>The carrier.</returns>
    public static VectorSource Literal(ReadOnlyMemory<sbyte> components) => new(
        address: default,
        components: components,
        discriminator: LiteralCase
    );

    /// <summary>Determines whether two carriers hold the same case with the same payload.</summary>
    /// <param name="left">The left carrier.</param>
    /// <param name="right">The right carrier.</param>
    /// <returns><see langword="true"/> when the two are equal.</returns>
    public static bool operator ==(VectorSource left, VectorSource right) => left.Equals(other: right);
    /// <summary>Determines whether two carriers differ in case or payload.</summary>
    /// <param name="left">The left carrier.</param>
    /// <param name="right">The right carrier.</param>
    /// <returns><see langword="true"/> when the two differ.</returns>
    public static bool operator !=(VectorSource left, VectorSource right) => !left.Equals(other: right);

    /// <inheritdoc/>
    public bool Equals(VectorSource other) => ((m_discriminator == other.m_discriminator) && (m_discriminator switch {
        CellCase => m_address.Equals(other: other.m_address),
        LiteralCase => m_components.Span.SequenceEqual(other: other.m_components.Span),
        _ => true,
    }));
    /// <inheritdoc/>
    public override bool Equals(object? obj) => ((obj is VectorSource other) && Equals(other: other));
    /// <inheritdoc/>
    /// <remarks>The literal case folds a per-process randomized hash, so this value is for dictionaries only and
    /// never reaches a state hash.</remarks>
    public override int GetHashCode() {
        var payload = new HashCode();

        payload.Add(value: m_discriminator);

        switch (m_discriminator) {
            case CellCase:
                payload.Add(value: m_address);

                break;
            case LiteralCase:
                payload.AddBytes(value: System.Runtime.InteropServices.MemoryMarshal.AsBytes(span: m_components.Span));

                break;
            default:
                break;
        }

        return payload.ToHashCode();
    }
    /// <inheritdoc/>
    public override string ToString() => (m_discriminator switch {
        CellCase => $"Cell({m_address.RowOrdinal}, {m_address.Key.Ordinal})",
        LiteralCase => $"Literal[{m_components.Length}]",
        _ => "none",
    });

    private InvalidOperationException Mismatch(string expected) => new(message: (HasValue
        ? $"A VectorSource holding {(IsCell ? nameof(Cell) : nameof(Literal))} was read as {expected}."
        : $"A default VectorSource holds no case and cannot be read as {expected}."
    ));
}
