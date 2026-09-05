using System.Numerics;
using System.Runtime.CompilerServices;

namespace Puck.Maths;

/// <summary>A hexagonal grid cell encoded as one integer in a dense, continuous walk through concentric rings.</summary>
/// <remarks>
/// <para>The origin is index zero. Ring <c>r</c> begins at <c>1 + 3r(r − 1)</c> at coordinate <c>(1, 1 − r)</c>,
/// and visits its <c>6r</c> cells counterclockwise in the Eisenstein basis of <see cref="HexagonalCoordinate"/>.
/// Thus indices 1 through 6 are that type's six directions, in order. Each ring ends at <c>(0, −r)</c>, one
/// step from the next ring's start <c>(1, −r)</c>. Every consecutive pair of admitted indices represents
/// neighbouring cells, and the disk through radius <c>r</c> occupies exactly indices <c>0</c> through <c>3r(r + 1)</c>.</para>
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
    public int Radius => (int)RadiusOf(value: Value);
    /// <summary>Gets the field norm, <c>Q² − QR + R²</c>, equal to squared Euclidean distance in unit-edge coordinates.</summary>
    public long Norm {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get {
            if (Value == 0) { return 0; }
            var (radius, offset) = Locate(value: Value);
            var step = (offset + 1) % radius;
            return (radius * radius) - (step * (radius - step));
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

        // Start the walk on the final edge, at (1, 1-radius), so successive rings meet at neighbouring cells.
        offset += ring - 1;
        if (offset >= (6 * ring)) { offset -= 6 * ring; }

        return new(value: RingStart(radius: ring) + offset);
    }

    /// <summary>Decodes this index to the Eisenstein coordinate used by <see cref="HexagonalCoordinate"/>.</summary>
    /// <returns>The cell's exact integer coordinate.</returns>
    public HexagonalCoordinate ToCoordinate() {
        if (Value == 0) { return default; }
        var (radius, offset) = Locate(value: Value);
        return Decode(radius: radius, offset: offset);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static HexagonalCoordinate Decode(long radius, long offset) {
        // Undo the ring's starting offset before decoding its six geometric edges.
        offset -= radius - 1;
        if (offset < 0) { offset += 6 * radius; }
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
        var (radius, offset) = Locate(value: Value);
        var shift = turns % HexagonalCoordinate.NeighborCount;
        if (shift < 0) { shift += HexagonalCoordinate.NeighborCount; }
        var rotated = (offset + (shift * radius)) % (6 * radius);

        return new(value: RingStart(radius: radius) + rotated);
    }

    /// <summary>Reflects across the real axis, directly on the ring offset: Eisenstein conjugation <c>(Q, R) → (Q − R, −R)</c>.</summary>
    /// <returns>The reflected cell. Two reflections are the identity.</returns>
    public HexagonalIndex Conjugate() {
        if (Value == 0) { return this; }
        var (radius, offset) = Locate(value: Value);
        var reflected = (2 * (radius - 1)) - offset;
        if (reflected < 0) { reflected += 6 * radius; }

        return new(value: RingStart(radius: radius) + reflected);
    }

    /// <summary>Exchanges Q and R directly on the ring offset.</summary>
    /// <returns>The reflected cell; exchanging twice is the identity.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public HexagonalIndex Swap() {
        if (Value == 0) { return this; }
        var (radius, offset) = Locate(value: Value);
        var reflected = (4 * radius) - 2 - offset;
        if (reflected < 0) { reflected += 6 * radius; }
        return new(value: RingStart(radius: radius) + reflected);
    }

    /// <summary>Multiplies both coordinates by a signed integer directly on the ring and offset.</summary>
    /// <param name="factor">The scale; negative values also apply a half-turn.</param>
    /// <returns>The scaled cell. Zero maps every cell to the origin.</returns>
    /// <exception cref="OverflowException">The result lies outside the complete-ring index domain.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public HexagonalIndex Scale(int factor) {
        if (Value == 0 || factor == 0) { return default; }
        var (radius, offset) = Locate(value: Value);
        var magnitude = Math.Abs(value: (long)factor);
        var scaledRadius = radius * magnitude;
        if (scaledRadius > MaxRadius) { throw new OverflowException("The scaled cell lies outside the complete-ring index domain."); }
        var scaledOffset = (magnitude * (offset + 1)) - 1;
        if (factor < 0) {
            scaledOffset += 3 * scaledRadius;
            if (scaledOffset >= 6 * scaledRadius) { scaledOffset -= 6 * scaledRadius; }
        }
        return new(value: RingStart(radius: scaledRadius) + scaledOffset);
    }

    /// <summary>Translates this cell by an exact coordinate displacement.</summary>
    /// <param name="displacement">The signed displacement in the Eisenstein basis.</param>
    /// <returns>The translated cell.</returns>
    /// <exception cref="OverflowException">The result lies outside the complete-ring index domain.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public HexagonalIndex Translate(HexagonalCoordinate displacement) {
        if (displacement.Q == displacement.R && displacement.Q >= 0) {
            if (Value == 0) { return FromCoordinate(coordinate: displacement); }
            var (radius, offset) = Locate(value: Value);
            // This interval is exactly Q >= 0 and R >= 0. The formula does not extend to the other sectors.
            if (offset >= radius - 1 && offset <= (3 * radius) - 1) {
                var amount = (long)displacement.Q;
                if (radius + amount > MaxRadius) { throw new OverflowException("The translated cell lies outside the complete-ring index domain."); }
                return new(value: Value + (amount * ((6 * radius) + (3 * amount) - 1)));
            }
            return FromCoordinate(coordinate: Decode(radius: radius, offset: offset) + displacement);
        }
        return FromCoordinate(coordinate: ToCoordinate() + displacement);
    }

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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long RadiusOf(long value) {
        if (value == 0) { return 0; }
        // Ring r begins when r(r-1) <= floor((value-1)/3). Inverting this inequality needs only ulong:
        // 1 + 4*floor((MaxValue-1)/3) < 2^64, whereas the unreduced discriminant 12*value-3 does not fit.
        var discriminant = 1 + (4 * ((ulong)(value - 1) / 3));
        return (long)((discriminant.SquareRoot() + 1) >> 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (long Radius, long Offset) Locate(long value) {
        var radius = RadiusOf(value: value);
        return (radius, value == 0 ? 0 : value - RingStart(radius: radius));
    }
}
