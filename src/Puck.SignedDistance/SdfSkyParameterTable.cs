using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Puck.SignedDistance;

/// <summary>One sky kind's resolved native records. The owning resolver supplies the typed elements; frame copying
/// and upload consume these same bytes without another parameter model or a floating-point carrier.</summary>
public interface ISdfSkyParameterTable {
    /// <summary>The registered kind name.</summary>
    string Kind { get; }
    /// <summary>The number of authored records, including records currently gated off.</summary>
    int Count { get; }
    /// <summary>The native size of one record, in bytes.</summary>
    int Stride { get; }
    /// <summary>The resolved records' exact native bytes.</summary>
    ReadOnlySpan<byte> Bytes { get; }
    /// <summary>Creates independent row storage for another retained frame of the same prepared stack.</summary>
    /// <returns>The copied table.</returns>
    ISdfSkyParameterTable Clone();
    /// <summary>Copies matching native records into existing storage.</summary>
    /// <param name="source">A table of the same kind, type and count.</param>
    /// <exception cref="ArgumentException">The source does not have the prepared table's shape.</exception>
    void CopyFrom(ISdfSkyParameterTable source);
}

/// <summary>Prepared native storage for one sky kind. The element type may live in the shader assembly; this
/// storage owner needs no shader compiler, GPU API or knowledge of the element's fields.</summary>
/// <typeparam name="T">The kind's unmanaged native record.</typeparam>
public sealed class SdfSkyParameterTable<T> : ISdfSkyParameterTable where T : unmanaged {
    private readonly T[] m_rows;

    /// <summary>Allocates one kind's records when the world structure is prepared.</summary>
    /// <param name="kind">The registered kind name.</param>
    /// <param name="count">The number of authored layers of that kind.</param>
    public SdfSkyParameterTable(string kind, int count) {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        Kind = kind;
        m_rows = new T[count];
    }

    /// <inheritdoc/>
    public string Kind { get; }
    /// <inheritdoc/>
    public int Count => m_rows.Length;
    /// <inheritdoc/>
    public int Stride => Unsafe.SizeOf<T>();
    /// <inheritdoc/>
    public ReadOnlySpan<byte> Bytes => MemoryMarshal.AsBytes(m_rows.AsSpan());
    /// <summary>The owning resolver's writable typed records. A published frame is copied before these change.</summary>
    public Span<T> Rows => m_rows;

    /// <inheritdoc/>
    public ISdfSkyParameterTable Clone() {
        var copy = new SdfSkyParameterTable<T>(Kind, Count);

        m_rows.CopyTo(copy.m_rows, 0);
        return copy;
    }
    /// <inheritdoc/>
    public void CopyFrom(ISdfSkyParameterTable source) {
        if ((source is not SdfSkyParameterTable<T> table) || (table.Kind != Kind) || (table.Count != Count)) {
            throw new ArgumentException("A sky parameter table copy requires the same kind, native type and row count.", nameof(source));
        }
        table.m_rows.CopyTo(m_rows, 0);
    }
}
