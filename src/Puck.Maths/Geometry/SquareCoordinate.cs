using System.Numerics;
using System.Runtime.CompilerServices;

namespace Puck.Maths;

/// <summary>An exact square-grid cell, represented by the Gaussian integer <c>X + Yi</c>.</summary>
/// <remarks>
/// The four neighbours are east, north, west and south. Length and Distance count cardinal steps
/// (Manhattan distance); Radius measures concentric square shells (Chebyshev distance).
/// Multiplication is the Gaussian product, composing rotation and scaling. Arithmetic and scalar queries
/// throw when their exact result cannot fit int. The default coordinate is the origin.
/// </remarks>
/// <param name="X">The horizontal component, increasing eastward.</param>
/// <param name="Y">The vertical component, increasing northward.</param>
public readonly record struct SquareCoordinate(int X, int Y)
    : IAdditionOperators<SquareCoordinate, SquareCoordinate, SquareCoordinate>,
      ISubtractionOperators<SquareCoordinate, SquareCoordinate, SquareCoordinate>,
      IMultiplyOperators<SquareCoordinate, SquareCoordinate, SquareCoordinate>,
      IUnaryNegationOperators<SquareCoordinate, SquareCoordinate>,
      IAdditiveIdentity<SquareCoordinate, SquareCoordinate>,
      IMultiplicativeIdentity<SquareCoordinate, SquareCoordinate> {
    /// <summary>The number of cardinal neighbours.</summary>
    public const int NeighborCount = 4;

    /// <summary>Gets the origin.</summary>
    public static SquareCoordinate AdditiveIdentity => default;
    /// <summary>Gets the Gaussian multiplicative identity, (1, 0).</summary>
    public static SquareCoordinate MultiplicativeIdentity => new(X: 1, Y: 0);
    /// <summary>Gets the cardinal-step distance from the origin, |X| + |Y|.</summary>
    /// <exception cref="OverflowException">The distance exceeds int.MaxValue.</exception>
    public int Length => checked((int)(Math.Abs(value: ((long)X)) + Math.Abs(value: ((long)Y))));
    /// <summary>Gets the square-shell radius, max(|X|, |Y|), also called Chebyshev distance.</summary>
    /// <exception cref="OverflowException">The radius exceeds int.MaxValue.</exception>
    public int Radius => checked((int)Math.Max(val1: Math.Abs(value: ((long)X)), val2: Math.Abs(value: ((long)Y))));
    /// <summary>Gets the Gaussian norm, X² + Y², the squared Euclidean distance.</summary>
    /// <exception cref="OverflowException">The norm exceeds int.MaxValue.</exception>
    public int Norm => checked((int)((((Int128)X) * X) + (((Int128)Y) * Y)));

    /// <summary>Formats the components without evaluating derived properties that may overflow.</summary>
    /// <returns>The invariant decimal X and Y components.</returns>
    public override string ToString() => FormattableString.Invariant(formattable: $"SquareCoordinate {{ X = {X}, Y = {Y} }}");
    /// <summary>Returns a cardinal unit step, counterclockwise from east.</summary>
    /// <param name="direction">Any signed direction index, reduced modulo four.</param>
    /// <returns>East, north, west or south for indices zero through three.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SquareCoordinate Direction(int direction) => (direction & 3) switch {
        0 => new(X: 1, Y: 0),
        1 => new(X: 0, Y: 1),
        2 => new(X: -1, Y: 0),
        _ => new(X: 0, Y: -1),
    };
    /// <summary>Computes cardinal-step distance between two cells.</summary>
    /// <param name="left">The first cell.</param>
    /// <param name="right">The second cell.</param>
    /// <returns>The Manhattan distance.</returns>
    /// <exception cref="OverflowException">The distance exceeds int.MaxValue.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Distance(SquareCoordinate left, SquareCoordinate right) =>
        checked((int)(Math.Abs(value: (((long)left.X) - right.X)) + Math.Abs(value: (((long)left.Y) - right.Y))));
    /// <summary>Moves one cardinal step.</summary>
    /// <param name="direction">Any signed direction index, reduced modulo four.</param>
    /// <returns>The adjacent cell.</returns>
    /// <exception cref="OverflowException">A resulting component exceeds int.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SquareCoordinate Neighbor(int direction) => (this + Direction(direction: direction));
    /// <summary>Rotates 90° counterclockwise about the origin.</summary>
    /// <returns>The coordinate (-Y, X).</returns>
    /// <exception cref="OverflowException">A resulting component exceeds int.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SquareCoordinate RotatedLeft() => new(X: checked(-Y), Y: X);
    /// <summary>Rotates 90° clockwise about the origin.</summary>
    /// <returns>The coordinate (Y, -X).</returns>
    /// <exception cref="OverflowException">A resulting component exceeds int.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SquareCoordinate RotatedRight() => new(X: Y, Y: checked(-X));
    /// <summary>Snaps a fractional position to its nearest grid cell, rounding ties to even on each axis.</summary>
    /// <param name="x">The fractional horizontal component.</param>
    /// <param name="y">The fractional vertical component.</param>
    /// <returns>The nearest cell.</returns>
    /// <exception cref="OverflowException">A rounded component exceeds int.</exception>
    public static SquareCoordinate Round(FixedQ4816 x, FixedQ4816 y) => new(
        X: checked((int)(FixedQ4816.Round(value: x).Value >> FixedQ4816.FractionBitCount)),
        Y: checked((int)(FixedQ4816.Round(value: y).Value >> FixedQ4816.FractionBitCount)));

    /// <summary>Adds the two coordinates.</summary>
    /// <param name="left">The first cell.</param>
    /// <param name="right">The displacement.</param>
    /// <returns>The coordinate sum.</returns>
    /// <exception cref="OverflowException">A resulting component exceeds int.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SquareCoordinate operator +(SquareCoordinate left, SquareCoordinate right) =>
        new(X: checked((left.X + right.X)), Y: checked((left.Y + right.Y)));
    /// <summary>Subtracts the two coordinates.</summary>
    /// <param name="left">The minuend.</param>
    /// <param name="right">The subtrahend.</param>
    /// <returns>The coordinate difference.</returns>
    /// <exception cref="OverflowException">A resulting component exceeds int.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SquareCoordinate operator -(SquareCoordinate left, SquareCoordinate right) =>
        new(X: checked((left.X - right.X)), Y: checked((left.Y - right.Y)));
    /// <summary>Negates both components.</summary>
    /// <param name="value">The cell to negate.</param>
    /// <returns>The cell opposite the origin.</returns>
    /// <exception cref="OverflowException">A resulting component exceeds int.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SquareCoordinate operator -(SquareCoordinate value) => new(X: checked(-value.X), Y: checked(-value.Y));
    /// <summary>Multiplies Gaussian integers, composing rotation and scaling.</summary>
    /// <param name="left">The multiplicand.</param>
    /// <param name="right">The multiplier.</param>
    /// <returns>(X₁X₂ − Y₁Y₂, X₁Y₂ + Y₁X₂).</returns>
    /// <exception cref="OverflowException">A resulting component exceeds int.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SquareCoordinate operator *(SquareCoordinate left, SquareCoordinate right) => new(
        X: checked((int)((((Int128)left.X) * right.X) - (((Int128)left.Y) * right.Y))),
        Y: checked((int)((((Int128)left.X) * right.Y) + (((Int128)left.Y) * right.X))));
    /// <summary>Scales both components by a signed integer.</summary>
    /// <param name="left">The coordinate.</param>
    /// <param name="right">The factor.</param>
    /// <returns>The scaled coordinate.</returns>
    /// <exception cref="OverflowException">A resulting component exceeds int.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SquareCoordinate operator *(SquareCoordinate left, int right) => new(X: checked((left.X * right)), Y: checked((left.Y * right)));
}
