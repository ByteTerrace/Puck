using System.Numerics;
using System.Runtime.CompilerServices;

namespace Puck.Maths;

/// <summary>A hexagonal grid cell encoded as one integer, ordered by distance from the origin and then around each ring.</summary>
/// <remarks>
/// <para>The origin is index zero. Ring <c>r</c> begins at <c>1 + 3r(r − 1)</c> at coordinate <c>(r, 0)</c>,
/// and visits its <c>6r</c> cells counterclockwise in the Eisenstein basis of <see cref="HexagonalCoordinate"/>.
/// Thus indices 1 through 6 are that type's six directions, in order. Consecutive indices within a ring are
/// neighbours; the transition to the next ring need not be a single step.</para>
/// <para>Only complete rings are admitted, through <see cref="MaxRadius"/>, so rotations and reflections are
/// closed over the entire index domain. Arithmetic transfers the coordinate ring operations to this encoding;
/// adding or multiplying <see cref="Value"/> directly does not perform those operations. Results outside the
/// admitted disk throw instead of wrapping. The default value is the origin.</para>
/// </remarks>
public readonly record struct HexagonalIndex
    : IAdditionOperators<HexagonalIndex, HexagonalIndex, HexagonalIndex>,
      ISubtractionOperators<HexagonalIndex, HexagonalIndex, HexagonalIndex>,
      IMultiplyOperators<HexagonalIndex, HexagonalIndex, HexagonalIndex>,
      IUnaryNegationOperators<HexagonalIndex, HexagonalIndex>,
      IAdditiveIdentity<HexagonalIndex, HexagonalIndex>,
      IMultiplicativeIdentity<HexagonalIndex, HexagonalIndex> {
    /// <summary>The largest radius whose complete ring fits in a nonnegative <see cref="long"/> index.</summary>
    public const int MaxRadius = 1_753_413_055;
    /// <summary>The last admitted index: <c>3·MaxRadius·(MaxRadius + 1)</c>.</summary>
    public const long MaxValue = 9_223_372_029_593_538_240L;

    /// <summary>Constructs a cell from its ring-ordered index.</summary>
    /// <param name="value">An index in <c>[0, MaxValue]</c>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> lies outside the admitted index range.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public HexagonalIndex(long value) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: value);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: value, other: MaxValue);
        Value = value;
    }

    /// <summary>Gets the ring-ordered index of this cell.</summary>
    public long Value { get; }
    /// <summary>Gets the exact hex-grid distance from the origin, without decoding the coordinate.</summary>
    public int Radius => (int)LayerSequence.CenteredHexagonal.LayerOf(index: Value);
    /// <summary>Gets the field norm, <c>Q² − QR + R²</c>, equal to squared Euclidean distance in unit-edge coordinates.</summary>
    public long Norm {
        get {
            var coordinate = ToCoordinate();
            var q = (long)coordinate.Q;
            var r = (long)coordinate.R;

            return ((q * q) - (q * r) + (r * r));
        }
    }
    /// <summary>Gets the origin cell.</summary>
    public static HexagonalIndex AdditiveIdentity => default;
    /// <summary>Gets the coordinate <c>(1, 0)</c>, the identity for the Eisenstein product.</summary>
    public static HexagonalIndex MultiplicativeIdentity => new(value: 1);

    /// <summary>Encodes an Eisenstein coordinate in the complete-ring index domain.</summary>
    /// <param name="coordinate">The cell to encode.</param>
    /// <returns>The unique index of the cell.</returns>
    /// <exception cref="OverflowException">The cell's hex-grid distance from the origin exceeds <see cref="MaxRadius"/>.</exception>
    public static HexagonalIndex FromCoordinate(HexagonalCoordinate coordinate) {
        var radius = coordinate.Length;
        if (radius > MaxRadius) { throw new OverflowException("The cell lies outside the complete-ring index domain."); }
        if (radius == 0) { return default; }

        var q = (long)coordinate.Q;
        var r = (long)coordinate.R;
        var ring = (long)radius;
        // Locate the cell along the perimeter; the (radius, 0) corner has offset zero.
        var offset = r >= 0
            ? (q > r ? r : ((2 * ring) - q))
            : (q < r ? ((3 * ring) - r) : ((5 * ring) + q));

        return new(value: RingStart(radius: ring) + offset);
    }

    /// <summary>Decodes this index to the Eisenstein coordinate used by <see cref="HexagonalCoordinate"/>.</summary>
    /// <returns>The cell's exact integer coordinate.</returns>
    public HexagonalCoordinate ToCoordinate() {
        if (Value == 0) { return default; }
        var (radius, offset) = LayerSequence.CenteredHexagonal.Locate(index: Value);
        var side = offset / radius;
        var step = (int)(offset - (side * radius));
        var ring = (int)radius;

        return side switch {
            0 => new(Q: ring, R: step),
            1 => new(Q: ring - step, R: ring),
            2 => new(Q: -step, R: ring - step),
            3 => new(Q: -ring, R: -step),
            4 => new(Q: step - ring, R: -ring),
            _ => new(Q: step, R: step - ring),
        };
    }

    /// <summary>Rotates about the origin by multiples of 60°, directly on the ring offset.</summary>
    /// <param name="turns">The signed number of counterclockwise turns; reduced modulo six.</param>
    /// <returns>The rotated cell. Six turns are the identity.</returns>
    public HexagonalIndex Rotate(int turns) {
        if (Value == 0) { return this; }
        var (radius, offset) = LayerSequence.CenteredHexagonal.Locate(index: Value);
        var shift = turns % HexagonalCoordinate.NeighborCount;
        if (shift < 0) { shift += HexagonalCoordinate.NeighborCount; }
        var rotated = (offset + (shift * radius)) % (6 * radius);

        return new(value: RingStart(radius: radius) + rotated);
    }

    /// <summary>Reflects across the real axis, directly on the ring offset: Eisenstein conjugation <c>(Q, R) → (Q − R, −R)</c>.</summary>
    /// <returns>The reflected cell. Two reflections are the identity.</returns>
    public HexagonalIndex Conjugate() {
        if (Value == 0) { return this; }
        var (radius, offset) = LayerSequence.CenteredHexagonal.Locate(index: Value);

        return new(value: RingStart(radius: radius) + (offset == 0 ? 0 : ((6 * radius) - offset)));
    }

    /// <summary>Translates this cell by an exact coordinate displacement.</summary>
    /// <param name="displacement">The signed displacement in the Eisenstein basis.</param>
    /// <returns>The translated cell.</returns>
    /// <exception cref="OverflowException">The result lies outside the complete-ring index domain.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public HexagonalIndex Translate(HexagonalCoordinate displacement) => FromCoordinate(coordinate: ToCoordinate() + displacement);

    /// <summary>Moves one hex-grid step in a direction.</summary>
    /// <param name="direction">A <see cref="HexagonalCoordinate.Direction(int)"/> index, reduced modulo six.</param>
    /// <returns>The neighbouring cell.</returns>
    /// <exception cref="OverflowException">The neighbour lies outside the complete-ring index domain.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public HexagonalIndex Neighbor(int direction) => Translate(displacement: HexagonalCoordinate.Direction(direction: direction));

    /// <summary>Computes the exact hex-grid distance between two encoded cells.</summary>
    /// <param name="left">The first cell.</param>
    /// <param name="right">The second cell.</param>
    /// <returns>The minimum number of neighbour steps, in <c>[0, 2·MaxRadius]</c>.</returns>
    public static long Distance(HexagonalIndex left, HexagonalIndex right) {
        var a = left.ToCoordinate();
        var b = right.ToCoordinate();
        var q = (long)a.Q - b.Q;
        var r = (long)a.R - b.R;

        return Math.Max(val1: Math.Max(val1: Math.Abs(value: q), val2: Math.Abs(value: r)), val2: Math.Abs(value: q - r));
    }

    /// <summary>Adds the represented coordinates.</summary>
    /// <param name="left">The first cell.</param>
    /// <param name="right">The displacement encoded as a cell.</param>
    /// <returns>The encoded coordinate sum.</returns>
    /// <exception cref="OverflowException">The sum lies outside the complete-ring index domain.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static HexagonalIndex operator +(HexagonalIndex left, HexagonalIndex right) => left.Translate(displacement: right.ToCoordinate());

    /// <summary>Subtracts the represented coordinates.</summary>
    /// <param name="left">The minuend.</param>
    /// <param name="right">The subtrahend.</param>
    /// <returns>The encoded displacement.</returns>
    /// <exception cref="OverflowException">The difference lies outside the complete-ring index domain.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static HexagonalIndex operator -(HexagonalIndex left, HexagonalIndex right) => FromCoordinate(coordinate: left.ToCoordinate() - right.ToCoordinate());

    /// <summary>Negates the represented coordinate by a half-turn of its ring.</summary>
    /// <param name="value">The cell to negate.</param>
    /// <returns>The cell opposite the origin.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static HexagonalIndex operator -(HexagonalIndex value) => value.Rotate(turns: 3);

    /// <summary>Multiplies the represented Eisenstein integers, composing rotation and scaling.</summary>
    /// <param name="left">The multiplicand.</param>
    /// <param name="right">The multiplier.</param>
    /// <returns>The encoded ring product.</returns>
    /// <exception cref="OverflowException">The product lies outside the complete-ring index domain.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static HexagonalIndex operator *(HexagonalIndex left, HexagonalIndex right) => FromCoordinate(coordinate: left.ToCoordinate() * right.ToCoordinate());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long RingStart(long radius) => 1 + (3 * radius * (radius - 1));
}
