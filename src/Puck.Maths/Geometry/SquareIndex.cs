using System.Numerics;
using System.Runtime.CompilerServices;

namespace Puck.Maths;

/// <summary>A signed square-grid cell encoded by a dense, continuous spiral through complete square shells.</summary>
/// <remarks>
/// The origin is zero. Shell r begins at (2r − 1)² at coordinate (r, 1 − r), visits 8r cells
/// counterclockwise, and ends at (r, −r), adjacent to the next shell's start. The square [-r, r]²
/// occupies exactly indices zero through (2r + 1)² − 1. Every successive index is a cardinal neighbour.
/// Radius is Chebyshev distance; Distance counts cardinal steps (Manhattan distance). Only complete shells
/// are admitted, preserving closure under rotations and reflections. Operators act on the represented
/// Gaussian coordinates; raw index arithmetic has a different meaning. Out-of-domain results throw.
/// </remarks>
public readonly record struct SquareIndex
    : IAdditionOperators<SquareIndex, SquareIndex, SquareIndex>,
      ISubtractionOperators<SquareIndex, SquareIndex, SquareIndex>,
      IMultiplyOperators<SquareIndex, SquareIndex, SquareIndex>,
      IUnaryNegationOperators<SquareIndex, SquareIndex>,
      IAdditiveIdentity<SquareIndex, SquareIndex>,
      IMultiplicativeIdentity<SquareIndex, SquareIndex> {
    /// <summary>The largest complete square-shell radius representable by a nonnegative long index.</summary>
    public const int MaxRadius = 1_518_500_249;
    /// <summary>The last admitted index: (2·MaxRadius + 1)² − 1.</summary>
    public const long MaxValue = 9_223_372_030_926_249_000L;

    /// <summary>Constructs a cell from its spiral index.</summary>
    /// <param name="value">An index in [0, MaxValue].</param>
    /// <exception cref="ArgumentOutOfRangeException">The index is outside the admitted range.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SquareIndex(long value) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: value);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: value, other: MaxValue);
        Value = value;
    }

    /// <summary>Gets the spiral index.</summary>
    public long Value { get; }
    /// <summary>Gets max(|X|, |Y|), the square-shell radius, without decoding.</summary>
    public int Radius => ((int)RadiusOf(value: Value));
    /// <summary>Gets X² + Y², the Gaussian norm, directly from the shell offset.</summary>
    public long Norm {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get {
            if (Value == 0) { return 0; }
            var (radius, offset) = Locate(value: Value);
            var component = (((offset + 1) % (2 * radius)) - radius);

            return ((radius * radius) + (component * component));
        }
    }
    /// <summary>Gets the origin.</summary>
    public static SquareIndex AdditiveIdentity => default;
    /// <summary>Gets (1, 0), the Gaussian multiplicative identity.</summary>
    public static SquareIndex MultiplicativeIdentity => new(value: 1);

    /// <summary>Encodes a signed square-grid coordinate.</summary>
    /// <param name="coordinate">The cell to encode.</param>
    /// <returns>The cell's unique index.</returns>
    /// <exception cref="OverflowException">The coordinate exceeds the complete-shell domain.</exception>
    public static SquareIndex FromCoordinate(SquareCoordinate coordinate) {
        var radius = ((long)coordinate.Radius);

        if (radius > MaxRadius) { throw new OverflowException(message: "The cell lies outside the complete-shell index domain."); }
        if (radius == 0) { return default; }
        var x = ((long)coordinate.X);
        var y = ((long)coordinate.Y);
        // Count from the bottom-right corner, excluding that corner until the final cell of the shell.
        var perimeter = (((x == radius) && (y > -radius)) ? (y + radius)
            : ((y == radius) ? ((3 * radius) - x)
            : ((x == -radius) ? ((5 * radius) - y)
            : ((7 * radius) + x))));

        return new(value: ((RingStart(radius: radius) + perimeter) - 1));
    }
    /// <summary>Decodes the index to its exact signed coordinate.</summary>
    /// <returns>The represented cell.</returns>
    public SquareCoordinate ToCoordinate() {
        if (Value == 0) { return default; }
        var (radius, offset) = Locate(value: Value);
        var perimeter = (offset + 1);

        if (perimeter <= (2 * radius)) { return new(X: ((int)radius), Y: ((int)(perimeter - radius))); }
        if (perimeter <= (4 * radius)) { return new(X: ((int)((3 * radius) - perimeter)), Y: ((int)radius)); }
        if (perimeter <= (6 * radius)) { return new(X: ((int)-radius), Y: ((int)((5 * radius) - perimeter))); }
        return new(X: ((int)(perimeter - (7 * radius))), Y: ((int)-radius));
    }
    /// <summary>Rotates by signed multiples of 90° counterclockwise directly on the shell offset.</summary>
    /// <param name="turns">Any signed turn count, reduced modulo four.</param>
    /// <returns>The rotated cell.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SquareIndex Rotate(int turns) {
        if (Value == 0) { return this; }
        var (radius, offset) = Locate(value: Value);
        var rotated = ((offset + (((turns & 3) * 2) * radius)) % (8 * radius));

        return new(value: (RingStart(radius: radius) + rotated));
    }
    /// <summary>Reflects across the X axis: Gaussian conjugation (X, Y) → (X, −Y).</summary>
    /// <returns>The reflected cell, computed directly from its offset.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SquareIndex Conjugate() {
        if (Value == 0) { return this; }
        var (radius, offset) = Locate(value: Value);
        var reflected = (((2 * radius) - 2) - offset);

        if (reflected < 0) { reflected += (8 * radius); }
        return new(value: (RingStart(radius: radius) + reflected));
    }
    /// <summary>Exchanges X and Y directly on the shell offset.</summary>
    /// <returns>The reflected cell.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SquareIndex Swap() {
        if (Value == 0) { return this; }
        var (radius, offset) = Locate(value: Value);
        var reflected = (((4 * radius) - 2) - offset);

        if (reflected < 0) { reflected += (8 * radius); }
        return new(value: (RingStart(radius: radius) + reflected));
    }
    /// <summary>Multiplies both coordinates by a signed integer directly on the radius and offset.</summary>
    /// <param name="factor">The scale; a negative value also applies a half-turn.</param>
    /// <returns>The scaled cell. Zero maps every cell to the origin.</returns>
    /// <exception cref="OverflowException">The result exceeds the complete-shell domain.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SquareIndex Scale(int factor) {
        if ((Value == 0) || (factor == 0)) { return default; }
        var (radius, offset) = Locate(value: Value);
        var magnitude = Math.Abs(value: ((long)factor));
        var scaledRadius = (radius * magnitude);

        if (scaledRadius > MaxRadius) { throw new OverflowException(message: "The scaled cell lies outside the complete-shell index domain."); }
        var scaledOffset = ((magnitude * (offset + 1)) - 1);

        if (factor < 0) {
            scaledOffset += (4 * scaledRadius);
            if (scaledOffset >= (8 * scaledRadius)) { scaledOffset -= (8 * scaledRadius); }
        }
        return new(value: (RingStart(radius: scaledRadius) + scaledOffset));
    }
    /// <summary>Translates the represented coordinate by a displacement.</summary>
    /// <param name="displacement">The signed displacement.</param>
    /// <returns>The translated cell.</returns>
    /// <exception cref="OverflowException">The result exceeds the complete-shell domain.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SquareIndex Translate(SquareCoordinate displacement) => FromCoordinate(coordinate: (ToCoordinate() + displacement));
    /// <summary>Moves one cardinal step.</summary>
    /// <param name="direction">Any signed direction index, reduced modulo four.</param>
    /// <returns>The adjacent cell.</returns>
    /// <exception cref="OverflowException">The neighbour exceeds the complete-shell domain.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SquareIndex Neighbor(int direction) => Translate(displacement: SquareCoordinate.Direction(direction: direction));
    /// <summary>Computes cardinal-step distance between encoded cells.</summary>
    /// <param name="left">The first cell.</param>
    /// <param name="right">The second cell.</param>
    /// <returns>The Manhattan distance, in [0, 4·MaxRadius].</returns>
    public static long Distance(SquareIndex left, SquareIndex right) {
        var a = left.ToCoordinate();
        var b = right.ToCoordinate();

        return (Math.Abs(value: (((long)a.X) - b.X)) + Math.Abs(value: (((long)a.Y) - b.Y)));
    }

    /// <summary>Adds the represented coordinates.</summary>
    /// <param name="left">The first cell.</param>
    /// <param name="right">The displacement encoded as a cell.</param>
    /// <returns>The encoded sum.</returns>
    /// <exception cref="OverflowException">The result exceeds the complete-shell domain.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SquareIndex operator +(SquareIndex left, SquareIndex right) => left.Translate(displacement: right.ToCoordinate());
    /// <summary>Subtracts the represented coordinates.</summary>
    /// <param name="left">The minuend.</param>
    /// <param name="right">The subtrahend.</param>
    /// <returns>The encoded difference.</returns>
    /// <exception cref="OverflowException">The result exceeds the complete-shell domain.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SquareIndex operator -(SquareIndex left, SquareIndex right) => FromCoordinate(coordinate: (left.ToCoordinate() - right.ToCoordinate()));
    /// <summary>Negates both coordinates by a half-turn.</summary>
    /// <param name="value">The cell to negate.</param>
    /// <returns>The opposite cell.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SquareIndex operator -(SquareIndex value) => value.Rotate(turns: 2);
    /// <summary>Multiplies Gaussian coordinates, composing rotation and scaling.</summary>
    /// <param name="left">The multiplicand.</param>
    /// <param name="right">The multiplier.</param>
    /// <returns>The encoded Gaussian product.</returns>
    /// <exception cref="OverflowException">The result exceeds the complete-shell domain.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SquareIndex operator *(SquareIndex left, SquareIndex right) => FromCoordinate(coordinate: (left.ToCoordinate() * right.ToCoordinate()));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long RadiusOf(long value) => ((long)((((ulong)value).SquareRoot() + 1) >> 1));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long RingStart(long radius) {
        var side = ((2 * radius) - 1);

        return (side * side);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (long Radius, long Offset) Locate(long value) {
        var radius = RadiusOf(value: value);

        return (radius, ((value == 0) ? 0 : (value - RingStart(radius: radius))));
    }
}
