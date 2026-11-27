using System.Numerics;

namespace Puck.SignedDistance;

/// <summary>How a displacement maps a baked vertex as it is drawn (<see cref="SdfDisplacement.Kind"/>); the value is the
/// first word of the displacement's entry in the frame's sampled table, which <c>sdfMeshDisplaced</c> switches on.</summary>
public enum SdfDisplacementKind : uint {
    /// <summary>A height field's columns (<see cref="SdfHeightField"/>): each vertex moved along Y by the column heights
    /// about it.</summary>
    HeightField = 0,
    /// <summary>A sheet of nodes (<see cref="SdfSheetDisplacement"/>): each vertex moved by the bilinear blend of the
    /// offsets of the four nodes about its place on the sheet.</summary>
    Sheet = 1,
}

/// <summary>
/// Values a static instance's baked mesh is displaced by as it is drawn, with no new mesh: a program's instance names one
/// (<see cref="SdfInstanceRange.Displacement"/>), the frame's sampled table carries its header and its values this frame
/// and the frame before (<see cref="Values"/>, which the bake never reads), and the renderer maps every vertex of the
/// instance's draws through it (<c>sdfMeshDisplaced</c>, <c>frame/sdf-mesh.hlsli</c>).
/// <para>Invariant: a displaced draw's box holds every point the displacement can move a vertex to; each kind's
/// <see cref="Bound"/> is the one place that box is widened, and its producer clamps every published value to it.</para>
/// </summary>
public abstract class SdfDisplacement {
    /// <summary>The words of every displacement's header in the frame's sampled table: its kind, its two counts, then nine
    /// floats a kind lays out as it needs (<see cref="WriteHeader"/>).</summary>
    public const int HeaderWords = 13;

    private protected SdfDisplacement() {
    }

    /// <summary>Gets how the displacement maps a vertex.</summary>
    public abstract SdfDisplacementKind Kind { get; }
    /// <summary>Gets the values the renderer displaces by, which a producer republishes as they move; never sampled by
    /// the bake.</summary>
    public abstract SdfSampledField Values { get; }
    /// <summary>Writes the displacement's header: <see cref="Kind"/> first, then the kind's own words.</summary>
    /// <param name="header">The header's words, <see cref="HeaderWords"/> long, float words as their bits.</param>
    public abstract void WriteHeader(Span<uint> header);
    /// <summary>Returns the box every displaced point of a mesh lies in, given the box its baked vertices lie in.</summary>
    /// <param name="low">The baked vertices' least corner, in the draw's object space.</param>
    /// <param name="high">The baked vertices' greatest corner.</param>
    /// <returns>The displaced box.</returns>
    public abstract (Vector3 Low, Vector3 High) Bound(Vector3 low, Vector3 high);
}

/// <summary>
/// A sheet of nodes that moves a baked mesh as it is drawn: a grid of <see cref="Columns"/> by <see cref="Rows"/> nodes
/// over the parallelogram <c>Origin + s·Across + t·Up</c> in the draw's object space, each node carrying an offset; a
/// vertex at grid place <c>(s·(Columns − 1), t·(Rows − 1))</c> moves by the bilinear blend of its cell's four node
/// offsets (clamped onto the grid, so a vertex past an edge takes the edge's), and its normal turns by the blend's
/// gradient. A net is drawn this way (a climbing net, a rope bridge, a banner): its mesh bakes once at rest and its
/// nodes move it.
/// </summary>
public sealed class SdfSheetDisplacement : SdfDisplacement {
    private readonly float[] m_values;
    private readonly Vector3 m_gradientS;
    private readonly Vector3 m_gradientT;

    /// <summary>Initializes a new instance of the <see cref="SdfSheetDisplacement"/> class at rest, every offset zero.</summary>
    /// <param name="columns">The nodes along <paramref name="across"/>, in [2, <see cref="SdfProgramBuilder.MaxSampledRegionDim"/> / 3].</param>
    /// <param name="rows">The nodes along <paramref name="up"/>, in [2, <see cref="SdfProgramBuilder.MaxSampledRegionDim"/>].</param>
    /// <param name="origin">The rest position of node (0, 0), in the draw's object space.</param>
    /// <param name="across">The rest edge to the last column's node (0 row).</param>
    /// <param name="up">The rest edge to the last row's node (0 column); not parallel to <paramref name="across"/>.</param>
    /// <param name="reach">The farthest any offset reaches, in world units: the box grows by it, and every published offset
    /// is clamped to it; finite and non-negative.</param>
    /// <exception cref="ArgumentOutOfRangeException">A count is out of range, the edges are parallel or not finite, or the
    /// reach is negative or not finite.</exception>
    public SdfSheetDisplacement(int columns, int rows, Vector3 origin, Vector3 across, Vector3 up, float reach) {
        ArgumentOutOfRangeException.ThrowIfLessThan(value: columns, other: 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(value: rows, other: 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: (3 * columns), other: SdfProgramBuilder.MaxSampledRegionDim);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: rows, other: SdfProgramBuilder.MaxSampledRegionDim);
        if (!float.IsFinite(f: reach) || (reach < 0f)) { throw new ArgumentOutOfRangeException(paramName: nameof(reach), message: "A sheet's reach must be finite and non-negative."); }

        var aa = Vector3.Dot(vector1: across, vector2: across);
        var ab = Vector3.Dot(vector1: across, vector2: up);
        var bb = Vector3.Dot(vector1: up, vector2: up);
        var determinant = ((aa * bb) - (ab * ab));

        if (!float.IsFinite(f: determinant) || (determinant <= (1e-12f * aa * bb)) || !float.IsFinite(f: origin.LengthSquared())) {
            throw new ArgumentOutOfRangeException(paramName: nameof(up), message: "A sheet's edges must be finite and span a plane.");
        }

        Columns = columns;
        Rows = rows;
        Origin = origin;
        Across = across;
        Up = up;
        Reach = reach;
        // The dual basis of the two edges in their plane, scaled to node units: s = (p − o)·gradientS counts columns.
        m_gradientS = ((((across * bb) - (up * ab)) / determinant) * (columns - 1));
        m_gradientT = ((((up * aa) - (across * ab)) / determinant) * (rows - 1));
        m_values = new float[checked(3 * columns * rows)];
        Values = new SdfSampledField(dimX: (3 * columns), dimY: 1, dimZ: rows, values: m_values);
    }

    /// <summary>Gets the nodes along <see cref="Across"/>.</summary>
    public int Columns { get; }
    /// <summary>Gets the nodes along <see cref="Up"/>.</summary>
    public int Rows { get; }
    /// <summary>Gets the rest position of node (0, 0).</summary>
    public Vector3 Origin { get; }
    /// <summary>Gets the rest edge to the last column.</summary>
    public Vector3 Across { get; }
    /// <summary>Gets the rest edge to the last row.</summary>
    public Vector3 Up { get; }
    /// <summary>Gets the farthest any offset reaches.</summary>
    public float Reach { get; }
    /// <inheritdoc/>
    public override SdfDisplacementKind Kind => SdfDisplacementKind.Sheet;
    /// <inheritdoc/>
    public override SdfSampledField Values { get; }

    /// <summary>Publishes new node offsets: a new <see cref="Values"/> snapshot when any moved, each clamped to
    /// <see cref="Reach"/> so the draw's box holds it. No program or mesh changes.</summary>
    /// <param name="offsets">One offset per node, row by row.</param>
    /// <returns>Whether any offset moved.</returns>
    /// <exception cref="ArgumentException"><paramref name="offsets"/> holds another count.</exception>
    public bool Publish(ReadOnlySpan<Vector3> offsets) {
        if (offsets.Length != (Columns * Rows)) {
            throw new ArgumentException(message: $"A {Columns}x{Rows} sheet takes {(Columns * Rows)} offsets, not {offsets.Length}.", paramName: nameof(offsets));
        }
        var moved = false;

        for (var node = 0; (node < offsets.Length); node++) {
            var offset = offsets[node];
            var length = offset.Length();

            if (!float.IsFinite(f: length)) { offset = Vector3.Zero; } else if (length > Reach) { offset *= (Reach / length); }

            var at = (3 * node);

            moved |= ((m_values[at] != offset.X) || (m_values[(at + 1)] != offset.Y) || (m_values[(at + 2)] != offset.Z));
            m_values[at] = offset.X;
            m_values[(at + 1)] = offset.Y;
            m_values[(at + 2)] = offset.Z;
        }
        if (moved) { _ = Values.Publish(values: m_values); }
        return moved;
    }
    /// <inheritdoc/>
    /// <remarks>The words after the kind: the columns, the rows, the origin, the reach, then the two gradients that count
    /// a point's grid place in node units (<c>s = (p − origin)·gradientS</c>).</remarks>
    public override void WriteHeader(Span<uint> header) {
        header[0] = (uint)Kind;
        header[1] = (uint)Columns;
        header[2] = (uint)Rows;
        header[3] = BitConverter.SingleToUInt32Bits(value: Origin.X);
        header[4] = BitConverter.SingleToUInt32Bits(value: Origin.Y);
        header[5] = BitConverter.SingleToUInt32Bits(value: Origin.Z);
        header[6] = BitConverter.SingleToUInt32Bits(value: Reach);
        header[7] = BitConverter.SingleToUInt32Bits(value: m_gradientS.X);
        header[8] = BitConverter.SingleToUInt32Bits(value: m_gradientS.Y);
        header[9] = BitConverter.SingleToUInt32Bits(value: m_gradientS.Z);
        header[10] = BitConverter.SingleToUInt32Bits(value: m_gradientT.X);
        header[11] = BitConverter.SingleToUInt32Bits(value: m_gradientT.Y);
        header[12] = BitConverter.SingleToUInt32Bits(value: m_gradientT.Z);
    }
    /// <inheritdoc/>
    public override (Vector3 Low, Vector3 High) Bound(Vector3 low, Vector3 high) => ((low - new Vector3(value: Reach)), (high + new Vector3(value: Reach)));
}
