using System.Numerics;

namespace Puck.Maths;

/// <summary>
/// An exact hexagonal grid coordinate: a point of the triangular lattice, realized as the Eisenstein integer
/// <c>Q + R·ω</c> (where <c>ω</c> is a primitive cube root of unity). Because it is a genuine number ring, a 60°
/// rotation is an exact integer multiply by a unit — no rounding, no drift — unlike the approximate
/// <see cref="FixedComplex"/>, and distance, neighbours, and the ring product are all exact integer arithmetic.
/// </summary>
/// <remarks>
/// The six nearest neighbours are the six units of the ring (the sixth roots of unity), enumerated by
/// <see cref="Direction(int)"/>; step to one with <see cref="Neighbor(int)"/>. <see cref="Length"/> is the hex-grid
/// step distance from the origin (the number of moves), while <see cref="Norm"/> is the field norm — the squared
/// Euclidean distance. <see cref="RotatedRight"/> and <see cref="RotatedLeft"/> turn 60° by multiplying by a unit, and
/// the ring product (<c>*</c>) composes rotation with scaling. Because it is the Eisenstein basis (with <c>ω</c> at
/// 120°), the neighbour at 60° is <c>(1, 1)</c> — build coordinates through the direction and neighbour helpers rather
/// than assuming a square-grid axial convention. <see cref="Round(FixedQ4816, FixedQ4816)"/> snaps a fractional
/// position to the nearest cell for turning a world position into a hex.
/// </remarks>
/// <param name="Q">The integer part.</param>
/// <param name="R">The coefficient of <c>ω</c>.</param>
public readonly record struct HexCoord(int Q, int R)
    : IAdditionOperators<HexCoord, HexCoord, HexCoord>,
      ISubtractionOperators<HexCoord, HexCoord, HexCoord>,
      IMultiplyOperators<HexCoord, HexCoord, HexCoord>,
      IUnaryNegationOperators<HexCoord, HexCoord>,
      IAdditiveIdentity<HexCoord, HexCoord>,
      IMultiplicativeIdentity<HexCoord, HexCoord> {
    /// <summary>The number of nearest neighbours — the six units of the ring.</summary>
    public const int NeighborCount = 6;

    // The six units (sixth roots of unity), in counterclockwise order starting from the +Q axis. Each is a 60° step.
    private static readonly HexCoord[] Units = [
        new(Q: 1, R: 0), new(Q: 1, R: 1), new(Q: 0, R: 1),
        new(Q: -1, R: 0), new(Q: -1, R: -1), new(Q: 0, R: -1),
    ];

    /// <summary>Gets the additive identity, the origin cell.</summary>
    public static HexCoord AdditiveIdentity => default;
    /// <summary>Gets the multiplicative identity, the unit <c>1</c> (the identity rotation).</summary>
    public static HexCoord MultiplicativeIdentity => new(Q: 1, R: 0);

    /// <summary>Gets the field norm — the squared Euclidean distance from the origin.</summary>
    public int Norm =>
        (((Q * Q) - (Q * R)) + (R * R));
    /// <summary>Gets the hex-grid step distance from the origin: the number of single moves to reach this cell.</summary>
    public int Length =>
        ((Math.Abs(Q) + Math.Abs(R) + Math.Abs(Q - R)) / 2);

    /// <summary>Negates a coordinate (the half-turn about the origin).</summary>
    /// <param name="value">The value to negate.</param>
    /// <returns>The componentwise negation.</returns>
    public static HexCoord operator -(HexCoord value) =>
        new(Q: -value.Q, R: -value.R);
    /// <summary>Adds two coordinates (translation).</summary>
    /// <param name="left">The first addend.</param>
    /// <param name="right">The second addend.</param>
    /// <returns>The componentwise sum.</returns>
    public static HexCoord operator +(HexCoord left, HexCoord right) =>
        new(Q: (left.Q + right.Q), R: (left.R + right.R));
    /// <summary>Subtracts <paramref name="right"/> from <paramref name="left"/>.</summary>
    /// <param name="left">The minuend.</param>
    /// <param name="right">The subtrahend.</param>
    /// <returns>The displacement from <paramref name="right"/> to <paramref name="left"/>.</returns>
    public static HexCoord operator -(HexCoord left, HexCoord right) =>
        new(Q: (left.Q - right.Q), R: (left.R - right.R));
    /// <summary>Multiplies two coordinates as Eisenstein integers: composes rotation with scaling.</summary>
    /// <param name="left">The multiplicand.</param>
    /// <param name="right">The multiplier; a unit rotates by a multiple of 60° without scaling.</param>
    /// <returns>The ring product <c>(Q₁ + R₁ω)(Q₂ + R₂ω)</c>, reduced by <c>ω² = −1 − ω</c>.</returns>
    public static HexCoord operator *(HexCoord left, HexCoord right) =>
        new(
        Q: ((left.Q * right.Q) - (left.R * right.R)),
        R: (((left.Q * right.R) + (left.R * right.Q)) - (left.R * right.R))
    );
    /// <summary>Scales a coordinate by an integer.</summary>
    /// <param name="left">The coordinate.</param>
    /// <param name="right">The integer factor.</param>
    /// <returns>The componentwise product.</returns>
    public static HexCoord operator *(HexCoord left, int right) =>
        new(Q: (left.Q * right), R: (left.R * right));

    /// <summary>Returns the hex-grid step distance between two cells.</summary>
    /// <param name="left">The first cell.</param>
    /// <param name="right">The second cell.</param>
    /// <returns>The number of single moves between them.</returns>
    public static int Distance(HexCoord left, HexCoord right) =>
        (left - right).Length;
    /// <summary>Returns the unit step in one of the six directions.</summary>
    /// <param name="direction">The direction index; taken modulo <see cref="NeighborCount"/>, so any integer is valid.</param>
    /// <returns>The unit coordinate for that direction, 60° apart from its neighbours.</returns>
    public static HexCoord Direction(int direction) =>
        Units[direction.FloorModulo(modulus: NeighborCount)];
    /// <summary>Returns the adjacent cell one step in a direction.</summary>
    /// <param name="direction">The direction index; taken modulo <see cref="NeighborCount"/>.</param>
    /// <returns>This cell plus the direction's unit step.</returns>
    public HexCoord Neighbor(int direction) =>
        (this + Direction(direction: direction));
    /// <summary>Rotates 60° counterclockwise about the origin (an exact multiply by the 60° unit).</summary>
    /// <returns>The rotated coordinate; six applications return to the start.</returns>
    public HexCoord RotatedLeft() =>
        (this * new HexCoord(Q: 1, R: 1));
    /// <summary>Rotates 60° clockwise about the origin (an exact multiply by the −60° unit).</summary>
    /// <returns>The rotated coordinate; six applications return to the start.</returns>
    public HexCoord RotatedRight() =>
        (this * new HexCoord(Q: 0, R: -1));
    /// <summary>Snaps a fractional lattice position to the nearest cell.</summary>
    /// <param name="q">The fractional integer part.</param>
    /// <param name="r">The fractional coefficient of <c>ω</c>.</param>
    /// <returns>The cell whose center is nearest the given position, resolving ties by the cube-rounding rule.</returns>
    public static HexCoord Round(FixedQ4816 q, FixedQ4816 r) {
        // Round in balanced cube coordinates (x + y + z = 0), then repair the axis with the largest rounding error so
        // the constraint holds exactly; this is the nearest-cell rule for the triangular lattice.
        var x = q;
        var y = (r - q);
        var z = -r;
        var roundedX = FixedQ4816.Round(value: x);
        var roundedY = FixedQ4816.Round(value: y);
        var roundedZ = FixedQ4816.Round(value: z);
        var errorX = FixedQ4816.Abs(value: (roundedX - x));
        var errorY = FixedQ4816.Abs(value: (roundedY - y));
        var errorZ = FixedQ4816.Abs(value: (roundedZ - z));

        if ((errorX > errorY) && (errorX > errorZ)) {
            roundedX = (-roundedY - roundedZ);
        } else if (errorY > errorZ) {
            roundedY = (-roundedX - roundedZ);
        } else {
            roundedZ = (-roundedX - roundedY);
        }

        return new HexCoord(
            Q: ((int)(roundedX.Value >> 16)),
            R: ((int)(-(roundedZ.Value >> 16)))
        );
    }
}
