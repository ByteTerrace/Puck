using System.Numerics;
using System.Text;

namespace Puck.Maths;

/// <summary>
/// A three-dimensional vector of <see cref="FixedQ4816"/> components. Every operation is integer-only fixed point, so it
/// is deterministic and bit-identical across machines — the basis for reproducible world-space simulation. The
/// <see cref="ToVector3"/> seam converts to single precision for presentation only (it never feeds back into the sim).
/// </summary>
/// <param name="X">The first component.</param>
/// <param name="Y">The second component.</param>
/// <param name="Z">The third component.</param>
public readonly record struct FixedVector3(FixedQ4816 X, FixedQ4816 Y, FixedQ4816 Z)
    : IAdditionOperators<FixedVector3, FixedVector3, FixedVector3>,
      ISubtractionOperators<FixedVector3, FixedVector3, FixedVector3>,
      IMultiplyOperators<FixedVector3, FixedQ4816, FixedVector3>,
      IDivisionOperators<FixedVector3, FixedQ4816, FixedVector3>,
      IUnaryNegationOperators<FixedVector3, FixedVector3>,
      IAdditiveIdentity<FixedVector3, FixedVector3> {
    /// <summary>Gets the additive identity, the zero vector.</summary>
    public static FixedVector3 AdditiveIdentity => default;
    /// <summary>Gets the full-width length, saturating when it exceeds the scalar carrier. Unlike taking the square
    /// root of <see cref="LengthSquared"/>, this rounds only the final raw Q32 root.</summary>
    public FixedQ4816 Length => (TryLength(length: out var length)
        ? length
        : FixedQ4816.MaxValue
    );
    /// <summary>Gets the exact raw Q32 sum of squares rounded once to Q16, saturating when it exceeds the scalar
    /// carrier. Use <see cref="TryLengthSquared"/> when overflow must be distinguished.</summary>
    public FixedQ4816 LengthSquared => (TryLengthSquared(squaredLength: out var squaredLength)
        ? squaredLength
        : FixedQ4816.MaxValue
    );
    /// <summary>Gets the unit vector along the X axis.</summary>
    public static FixedVector3 UnitX => new(
        X: FixedQ4816.One,
        Y: FixedQ4816.Zero,
        Z: FixedQ4816.Zero
    );
    /// <summary>Gets the unit vector along the Y axis.</summary>
    public static FixedVector3 UnitY => new(
        X: FixedQ4816.Zero,
        Y: FixedQ4816.One,
        Z: FixedQ4816.Zero
    );
    /// <summary>Gets the unit vector along the Z axis.</summary>
    public static FixedVector3 UnitZ => new(
        X: FixedQ4816.Zero,
        Y: FixedQ4816.Zero,
        Z: FixedQ4816.One
    );
    /// <summary>Gets the zero vector.</summary>
    public static FixedVector3 Zero => AdditiveIdentity;

    /// <summary>Adds two vectors componentwise.</summary>
    /// <param name="left">The first addend.</param>
    /// <param name="right">The second addend.</param>
    /// <returns>The componentwise sum.</returns>
    public static FixedVector3 operator +(FixedVector3 left, FixedVector3 right) =>
        new(
            X: (left.X + right.X),
            Y: (left.Y + right.Y),
            Z: (left.Z + right.Z)
        );
    /// <summary>Adds two vectors componentwise, throwing when any component sum leaves the carrier.</summary>
    /// <param name="left">The first addend.</param>
    /// <param name="right">The second addend.</param>
    /// <returns>The componentwise sum.</returns>
    /// <exception cref="OverflowException">A component sum is outside the representable range. Components are summed
    /// in X, Y, Z order, so the first one that overflows is the one that throws.</exception>
    public static FixedVector3 operator checked +(FixedVector3 left, FixedVector3 right) =>
        new(
            X: checked((left.X + right.X)),
            Y: checked((left.Y + right.Y)),
            Z: checked((left.Z + right.Z))
        );
    /// <summary>Subtracts <paramref name="right"/> from <paramref name="left"/> componentwise.</summary>
    /// <param name="left">The minuend.</param>
    /// <param name="right">The subtrahend.</param>
    /// <returns>The componentwise difference.</returns>
    public static FixedVector3 operator -(FixedVector3 left, FixedVector3 right) =>
        new(
            X: (left.X - right.X),
            Y: (left.Y - right.Y),
            Z: (left.Z - right.Z)
        );
    /// <summary>Subtracts <paramref name="right"/> from <paramref name="left"/> componentwise, throwing when any
    /// component difference leaves the carrier.</summary>
    /// <param name="left">The minuend.</param>
    /// <param name="right">The subtrahend.</param>
    /// <returns>The componentwise difference.</returns>
    /// <exception cref="OverflowException">A component difference is outside the representable range. Components are
    /// subtracted in X, Y, Z order, so the first one that overflows is the one that throws.</exception>
    public static FixedVector3 operator checked -(FixedVector3 left, FixedVector3 right) =>
        new(
            X: checked((left.X - right.X)),
            Y: checked((left.Y - right.Y)),
            Z: checked((left.Z - right.Z))
        );
    /// <summary>Negates a vector componentwise.</summary>
    /// <param name="value">The vector to negate.</param>
    /// <returns>The vector pointing the opposite way, each component negated.</returns>
    public static FixedVector3 operator -(FixedVector3 value) =>
        new(
            X: (-value.X),
            Y: (-value.Y),
            Z: (-value.Z)
        );
    /// <summary>Negates a vector componentwise, throwing when any component has no representable negation.</summary>
    /// <param name="value">The vector to negate.</param>
    /// <returns>The vector pointing the opposite way, each component negated.</returns>
    /// <exception cref="OverflowException">A component is <see cref="FixedQ4816.MinValue"/>. Components are negated
    /// in X, Y, Z order, so the first such component is the one that throws.</exception>
    public static FixedVector3 operator checked -(FixedVector3 value) =>
        new(
            X: checked(-value.X),
            Y: checked(-value.Y),
            Z: checked(-value.Z)
        );
    /// <summary>Scales a vector by a scalar.</summary>
    /// <param name="vector">The vector to scale.</param>
    /// <param name="scalar">The scale factor.</param>
    /// <returns>The scaled vector.</returns>
    public static FixedVector3 operator *(FixedVector3 vector, FixedQ4816 scalar) =>
        new(
            X: (vector.X * scalar),
            Y: (vector.Y * scalar),
            Z: (vector.Z * scalar)
        );
    /// <summary>Scales a vector by a scalar, throwing when any rounded component product leaves the carrier.</summary>
    /// <param name="vector">The vector to scale.</param>
    /// <param name="scalar">The scale factor.</param>
    /// <returns>The scaled vector, each component rounded to nearest with ties to even.</returns>
    /// <exception cref="OverflowException">A rounded component product is outside the representable range. Components
    /// are scaled in X, Y, Z order, so the first one that overflows is the one that throws.</exception>
    public static FixedVector3 operator checked *(FixedVector3 vector, FixedQ4816 scalar) =>
        new(
            X: checked((vector.X * scalar)),
            Y: checked((vector.Y * scalar)),
            Z: checked((vector.Z * scalar))
        );
    /// <summary>Divides a vector by a scalar componentwise.</summary>
    /// <param name="vector">The dividend vector.</param>
    /// <param name="scalar">The divisor.</param>
    /// <returns>The vector with each component divided by <paramref name="scalar"/> — genuine per-component division rounded to nearest, more accurate than multiplying by a reciprocal.</returns>
    /// <exception cref="System.DivideByZeroException"><paramref name="scalar"/> is zero.</exception>
    public static FixedVector3 operator /(FixedVector3 vector, FixedQ4816 scalar) =>
        new(
            X: (vector.X / scalar),
            Y: (vector.Y / scalar),
            Z: (vector.Z / scalar)
        );

    private static long NudgeOffMinValue(long raw) =>
        ((raw == long.MinValue)
            ? (raw + 1L)
            : raw
        );
    /// <summary>Prints the three declared components, and nothing derived from them.</summary>
    /// <param name="builder">The builder the record's <c>ToString</c> assembles into.</param>
    /// <returns><see langword="true"/>, because a member was written.</returns>
    /// <remarks>Hand-written because the compiler-synthesized body walks every public readable instance property — the saturating <see cref="Length"/> and <see cref="LengthSquared"/> included — which would run the full norm computation on every format and print the saturation sentinel <see cref="FixedQ4816.MaxValue"/> in the position of a measured length.</remarks>
    private bool PrintMembers(StringBuilder builder) {
        builder.Append(value: "X = ");
        builder.Append(value: X.ToString());
        builder.Append(value: ", Y = ");
        builder.Append(value: Y.ToString());
        builder.Append(value: ", Z = ");
        builder.Append(value: Z.ToString());

        return true;
    }

    /// <summary>Takes the absolute value of each component.</summary>
    /// <param name="value">The vector whose components are made non-negative.</param>
    /// <returns>The vector of <see cref="FixedQ4816.Abs"/> applied to each component.</returns>
    /// <exception cref="OverflowException">A component is <see cref="FixedQ4816.MinValue"/>, whose magnitude is
    /// unrepresentable.</exception>
    public static FixedVector3 Abs(FixedVector3 value) =>
        new(
            X: FixedQ4816.Abs(value: value.X),
            Y: FixedQ4816.Abs(value: value.Y),
            Z: FixedQ4816.Abs(value: value.Z)
        );
    /// <summary>Restricts each component to the inclusive range its bounds' matching components give.</summary>
    /// <param name="value">The vector to clamp.</param>
    /// <param name="minimum">The inclusive lower bound, per component.</param>
    /// <param name="maximum">The inclusive upper bound, per component.</param>
    /// <returns>The vector of <see cref="FixedQ4816.Clamp"/> applied to each component.</returns>
    /// <exception cref="ArgumentException">A component of <paramref name="minimum"/> is greater than the matching
    /// component of <paramref name="maximum"/>.</exception>
    public static FixedVector3 Clamp(FixedVector3 value, FixedVector3 minimum, FixedVector3 maximum) =>
        new(
            X: FixedQ4816.Clamp(
                value: value.X,
                minimum: minimum.X,
                maximum: maximum.X
            ),
            Y: FixedQ4816.Clamp(
                value: value.Y,
                minimum: minimum.Y,
                maximum: maximum.Y
            ),
            Z: FixedQ4816.Clamp(
                value: value.Z,
                minimum: minimum.Z,
                maximum: maximum.Z
            )
        );
    /// <summary>The cross product of two vectors — integer-only, deterministic.</summary>
    /// <param name="left">The first vector.</param>
    /// <param name="right">The second vector.</param>
    /// <returns>The vector cross product — the wedge product <c>left ∧ right</c> read as an axis
    /// (see <see cref="FixedVector2.Wedge"/> for the planar case). Each component accumulates two Q32 products and
    /// rounds once to Q16.</returns>
    public static FixedVector3 Cross(FixedVector3 left, FixedVector3 right) {
        const ulong NarrowLimit = (1UL << 31);
        var combinedMagnitude = FixedVectorMath.RawMagnitude(value: left.X.Value) |
                                 FixedVectorMath.RawMagnitude(value: left.Y.Value) |
                                 FixedVectorMath.RawMagnitude(value: left.Z.Value) |
                                 FixedVectorMath.RawMagnitude(value: right.X.Value) |
                                 FixedVectorMath.RawMagnitude(value: right.Y.Value) |
                                 FixedVectorMath.RawMagnitude(value: right.Z.Value);

        if (combinedMagnitude < NarrowLimit) {
            return new(
                X: FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked(((left.Y.Value * right.Z.Value) - (left.Z.Value * right.Y.Value))))),
                Y: FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked(((left.Z.Value * right.X.Value) - (left.X.Value * right.Z.Value))))),
                Z: FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked(((left.X.Value * right.Y.Value) - (left.Y.Value * right.X.Value)))))
            );
        }

        return new(
            X: FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked(((((Int128)left.Y.Value) * right.Z.Value) - (((Int128)left.Z.Value) * right.Y.Value))))),
            Y: FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked(((((Int128)left.Z.Value) * right.X.Value) - (((Int128)left.X.Value) * right.Z.Value))))),
            Z: FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked(((((Int128)left.X.Value) * right.Y.Value) - (((Int128)left.Y.Value) * right.X.Value)))))
        );
    }
    /// <summary>Divides one vector by another componentwise.</summary>
    /// <param name="left">The dividend vector.</param>
    /// <param name="right">The divisor vector.</param>
    /// <returns>The vector of <see cref="FixedQ4816"/> quotients, each rounded to nearest with ties to even and
    /// wrapping on overflow, exactly as the scalar <c>/</c> operator does.</returns>
    /// <exception cref="DivideByZeroException">A component of <paramref name="right"/> is zero.</exception>
    public static FixedVector3 Divide(FixedVector3 left, FixedVector3 right) =>
        new(
            X: (left.X / right.X),
            Y: (left.Y / right.Y),
            Z: (left.Z / right.Z)
        );
    /// <summary>The dot product of two vectors — integer-only, deterministic.</summary>
    /// <param name="left">The first vector.</param>
    /// <param name="right">The second vector.</param>
    /// <returns>The scalar dot product, with all three Q32 products accumulated before a single Q16 rounding.</returns>
    public static FixedQ4816 Dot(FixedVector3 left, FixedVector3 right) {
        const ulong NarrowLimit = (1UL << 30);
        var combinedMagnitude = FixedVectorMath.RawMagnitude(value: left.X.Value) |
                                 FixedVectorMath.RawMagnitude(value: left.Y.Value) |
                                 FixedVectorMath.RawMagnitude(value: left.Z.Value) |
                                 FixedVectorMath.RawMagnitude(value: right.X.Value) |
                                 FixedVectorMath.RawMagnitude(value: right.Y.Value) |
                                 FixedVectorMath.RawMagnitude(value: right.Z.Value);

        if (combinedMagnitude < NarrowLimit) {
            return FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked(
                (((left.X.Value * right.X.Value) + (left.Y.Value * right.Y.Value)) + (left.Z.Value * right.Z.Value)))));
        }

        return FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked(
            (((((Int128)left.X.Value) * right.X.Value) +
            (((Int128)left.Y.Value) * right.Y.Value)) +
            (((Int128)left.Z.Value) * right.Z.Value)))));
    }
    /// <summary>Intersects the ray <c>origin + t·direction</c>, <c>t ≥ 0</c>, with the plane through
    /// <paramref name="planePoint"/> whose normal is <paramref name="planeNormal"/> — integer-only, deterministic.</summary>
    /// <param name="origin">The ray's origin.</param>
    /// <param name="direction">The ray's direction; it need not be unit length, and <paramref name="distance"/> is
    /// measured in multiples of it.</param>
    /// <param name="planePoint">Any point on the plane.</param>
    /// <param name="planeNormal">The plane's normal; it need not be unit length, and either orientation names the same
    /// plane.</param>
    /// <param name="distance">The ray parameter <c>t</c> of the hit when this returns <see langword="true"/>, zero
    /// otherwise: <c>n / d</c> rounded once to nearest with ties to even, where <c>n</c> is
    /// <see cref="Dot"/><c>(planePoint − origin, planeNormal)</c> (the difference wrapping as the <c>-</c> operator
    /// does) and <c>d</c> is <see cref="Dot"/><c>(direction, planeNormal)</c>.</param>
    /// <returns><see langword="false"/> when <c>d</c> is zero (the ray runs parallel to the plane, or a vector is
    /// zero), when the exact quotient <c>n / d</c> is negative (the plane lies behind the origin), or when the rounded
    /// quotient leaves the carrier; <see langword="true"/> otherwise, including an origin on the plane
    /// (<c>t = 0</c>).</returns>
    public static bool TryIntersectPlane(FixedVector3 origin, FixedVector3 direction, FixedVector3 planePoint, FixedVector3 planeNormal, out FixedQ4816 distance) {
        var denominator = Dot(
            left: direction,
            right: planeNormal
        ).Value;
        var numerator = Dot(
            left: (planePoint - origin),
            right: planeNormal
        ).Value;

        distance = FixedQ4816.Zero;

        if (
            (denominator == 0L) ||
            ((numerator != 0L) && ((numerator < 0L) != (denominator < 0L)))
        ) {
            return false;
        }
        if (
            !FusedArithmetic.TryDivideMagnitudeRounded(
                denominatorMagnitude: FixedVectorMath.RawMagnitude(value: denominator),
                fractionBitCount: FixedQ4816.FractionBitCount,
                numeratorMagnitude: FixedVectorMath.RawMagnitude(value: numerator),
                quotient: out var magnitude
            ) ||
            !FusedArithmetic.TryNarrowSignedMagnitude(
                magnitude: magnitude,
                negative: false,
                result: out var raw
            )
        ) {
            return false;
        }

        distance = FixedQ4816.FromRawBits(value: raw);

        return true;
    }
    /// <summary>Converts a single-precision <see cref="System.Numerics.Vector3"/> componentwise into fixed point —
    /// the inbound counterpart of <see cref="ToVector3"/>, and the ONE door an authored or renderer-side float takes
    /// into the deterministic world, so the rounding a caller gets is not a per-caller decision.</summary>
    /// <param name="value">The vector to convert.</param>
    /// <returns>The nearest fixed-point vector, each component rounded to nearest with ties to even by
    /// <see cref="FixedQ4816.FromDouble"/>, saturating at the carrier's extremes; a not-a-number component
    /// becomes zero.</returns>
    public static FixedVector3 FromVector3(System.Numerics.Vector3 value) =>
        new(
            X: FixedQ4816.FromDouble(value: value.X),
            Y: FixedQ4816.FromDouble(value: value.Y),
            Z: FixedQ4816.FromDouble(value: value.Z)
        );
    /// <summary>Linearly interpolates each component from <paramref name="from"/> to <paramref name="to"/> by <paramref name="amount"/>.</summary>
    /// <param name="from">The vector returned when <paramref name="amount"/> is zero.</param>
    /// <param name="to">The vector returned when <paramref name="amount"/> is one.</param>
    /// <param name="amount">The interpolation fraction; values outside <c>[0, 1]</c> extrapolate.</param>
    /// <returns>The componentwise <see cref="FixedQ4816.Lerp"/> — exactly <paramref name="from"/> at zero and <paramref name="to"/> at one.</returns>
    public static FixedVector3 Lerp(FixedVector3 from, FixedVector3 to, FixedQ4816 amount) =>
        new(
            X: FixedQ4816.Lerp(
                from: from.X,
                to: to.X,
                amount: amount
            ),
            Y: FixedQ4816.Lerp(
                from: from.Y,
                to: to.Y,
                amount: amount
            ),
            Z: FixedQ4816.Lerp(
                from: from.Z,
                to: to.Z,
                amount: amount
            )
        );
    /// <summary>Takes the greater of each pair of matching components.</summary>
    /// <param name="left">The first vector.</param>
    /// <param name="right">The second vector.</param>
    /// <returns>The vector of <see cref="FixedQ4816.Max"/> applied to each component pair.</returns>
    public static FixedVector3 Max(FixedVector3 left, FixedVector3 right) =>
        new(
            X: FixedQ4816.Max(
                x: left.X,
                y: right.X
            ),
            Y: FixedQ4816.Max(
                x: left.Y,
                y: right.Y
            ),
            Z: FixedQ4816.Max(
                x: left.Z,
                y: right.Z
            )
        );
    /// <summary>Takes the greatest of a vector's three components.</summary>
    /// <param name="value">The vector to read.</param>
    /// <returns>The largest of <see cref="X"/>, <see cref="Y"/> and <see cref="Z"/>.</returns>
    public static FixedQ4816 MaxComponent(FixedVector3 value) =>
        FixedQ4816.Max(
            x: value.X,
            y: FixedQ4816.Max(
                x: value.Y,
                y: value.Z
            )
        );
    /// <summary>Moves a vector toward a target by no more than a non-negative distance.</summary>
    /// <param name="current">The current vector.</param>
    /// <param name="target">The target vector.</param>
    /// <param name="maxDelta">The greatest distance to move.</param>
    /// <returns>The target when it is within range; otherwise, the point <paramref name="maxDelta"/> toward it.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxDelta"/> is negative.</exception>
    /// <remarks>Ordering is read per axis from the two raw carrier readings directly, in the unsigned 64-bit domain —
    /// never from a componentwise <c>target − current</c>, whose true per-axis magnitude can exceed what the signed
    /// 64-bit carrier can hold even though both endpoints are individually representable (the opposing carrier
    /// extremes, for instance). Whenever every axis separation fits the signed carrier, the ordinary
    /// delta/<see cref="Length"/> path below is exact and bit-for-bit identical to comparing the componentwise
    /// difference directly. Only when some axis separation does not fit does this fall back to a widened-domain step: a
    /// single axis separation that already exceeds a representable <paramref name="maxDelta"/> (whose raw is at most
    /// <see cref="long.MaxValue"/>) refutes landing outright, since the 3D distance can only be at least as large as
    /// its largest axis component.</remarks>
    public static FixedVector3 MoveToward(FixedVector3 current, FixedVector3 target, FixedQ4816 maxDelta) {
        // The parameter name is passed explicitly: the throw helper's caller-argument expression would otherwise report
        // the literal string "maxDelta.Value", a property expression rather than a parameter of this method.
        ArgumentOutOfRangeException.ThrowIfNegative(
            value: maxDelta.Value,
            paramName: nameof(maxDelta)
        );

        var (separationX, targetGreaterX) = FixedVectorMath.RawSeparation(
            currentRaw: current.X.Value,
            targetRaw: target.X.Value
        );
        var (separationY, targetGreaterY) = FixedVectorMath.RawSeparation(
            currentRaw: current.Y.Value,
            targetRaw: target.Y.Value
        );
        var (separationZ, targetGreaterZ) = FixedVectorMath.RawSeparation(
            currentRaw: current.Z.Value,
            targetRaw: target.Z.Value
        );
        var maxSeparation = Math.Max(
            val1: separationX,
            val2: Math.Max(
                val1: separationY,
                val2: separationZ
            )
        );

        if (maxSeparation <= ((ulong)long.MaxValue)) {
            var delta = (target - current);
            var distance = delta.Length;

            return (((distance <= maxDelta) || (distance <= FixedQ4816.Zero))
                ? target
                : (current + ((delta / distance) * maxDelta))
            );
        }

        // Some axis separation exceeds what the signed 64-bit carrier can hold, so it also exceeds every
        // representable maxDelta.Value (at most long.MaxValue) — landing is therefore impossible, and the step is a
        // proportional share of maxDelta along the unit direction of the (halved, to fit the sign/magnitude
        // reconstruction) per-axis separations. The addition below cannot overflow: the true separation on every axis
        // that moves is strictly larger than that axis's own step, so the landed point stays strictly between
        // current and target on the real line, a range both endpoints already witness is representable.
        var dx = FixedVectorMath.SignedFromMagnitude(
            magnitude: (separationX >> 1),
            negative: !targetGreaterX
        );
        var dy = FixedVectorMath.SignedFromMagnitude(
            magnitude: (separationY >> 1),
            negative: !targetGreaterY
        );
        var dz = FixedVectorMath.SignedFromMagnitude(
            magnitude: (separationZ >> 1),
            negative: !targetGreaterZ
        );

        var (unitX, unitY, unitZ) = FixedVectorMath.Normalize(
            x: dx,
            y: dy,
            z: dz
        );
        var stepX = (FixedQ4816.FromRawBits(value: unitX) * maxDelta);
        var stepY = (FixedQ4816.FromRawBits(value: unitY) * maxDelta);
        var stepZ = (FixedQ4816.FromRawBits(value: unitZ) * maxDelta);

        return new(
            X: (current.X + stepX),
            Y: (current.Y + stepY),
            Z: (current.Z + stepZ)
        );
    }
    /// <summary>Multiplies two vectors componentwise (the Hadamard product).</summary>
    /// <param name="left">The first factor.</param>
    /// <param name="right">The second factor.</param>
    /// <returns>The vector of <see cref="FixedQ4816"/> products, each rounded to nearest with ties to even and
    /// wrapping on overflow, exactly as the scalar <c>*</c> operator does.</returns>
    public static FixedVector3 Multiply(FixedVector3 left, FixedVector3 right) =>
        new(
            X: (left.X * right.X),
            Y: (left.Y * right.Y),
            Z: (left.Z * right.Z)
        );
    /// <summary>Normalizes the vector to Q16 unit length at every representable input scale. The calculation applies
    /// one common power-of-two scale before its exact sum of squares, so tiny directions do not disappear and extreme
    /// directions do not overflow. Zero normalizes to <see cref="Zero"/>.</summary>
    /// <returns>The unit-length vector along the same direction, or <see cref="Zero"/> when this vector is zero.</returns>
    public FixedVector3 Normalize() {
        var (x, y, z) = FixedVectorMath.Normalize(
            x: X.Value,
            y: Y.Value,
            z: Z.Value
        );

        if ((x | y | z) == 0L) {
            return Zero;
        }

        return new(
            X: FixedQ4816.FromRawBits(value: x),
            Y: FixedQ4816.FromRawBits(value: y),
            Z: FixedQ4816.FromRawBits(value: z)
        );
    }
    /// <summary>Builds an orthonormal basis with <paramref name="normal"/> as its third axis: two mutually
    /// perpendicular tangent directions, deterministic in the input raws.</summary>
    /// <param name="normal">The axis the two tangents are built perpendicular to. Not required to be unit — the
    /// branch selection below does not depend on it, though a non-unit input leaves <paramref name="tangent2"/>
    /// scaled by its magnitude (see remarks).</param>
    /// <param name="tangent1">The first tangent, unit length whenever <paramref name="normal"/> is non-zero.</param>
    /// <param name="tangent2">The second tangent, <c><see cref="Cross"/>(tangent1, normal)</c> — perpendicular to
    /// both <paramref name="tangent1"/> and <paramref name="normal"/>, but not renormalized.</param>
    /// <remarks>Branches on which axis component of <paramref name="normal"/> has the smallest magnitude and crosses
    /// that axis with <paramref name="normal"/> to build <paramref name="tangent1"/>, then normalizes — the same
    /// deterministic perpendicular construction <see cref="FixedQuaternion.FromTo"/> uses for its antiparallel
    /// fallback, factored out here so both callers share one implementation. When <paramref name="normal"/> is unit,
    /// <paramref name="tangent2"/> is unit too, to within the fused-rounding envelope <see cref="Cross"/> already
    /// carries; when it is not, <paramref name="tangent2"/>'s magnitude tracks <paramref name="normal"/>'s. The
    /// branch boundary is a discontinuity in which axis pair is CHOSEN, not a claim that the chosen vectors vary
    /// continuously across it.</remarks>
    public static void OrthonormalBasis(FixedVector3 normal, out FixedVector3 tangent1, out FixedVector3 tangent2) {
        // FixedQ4816.Abs throws at MinValue, whose magnitude has no representable positive counterpart; the branch
        // selection only ever COMPARES magnitudes, so the unsigned face is both sufficient and total.
        var absX = FusedArithmetic.RawMagnitude(value: normal.X.Value);
        var absY = FusedArithmetic.RawMagnitude(value: normal.Y.Value);
        var absZ = FusedArithmetic.RawMagnitude(value: normal.Z.Value);
        // MinValue negates to itself under unchecked wraparound — the one raw value whose sign a wrap gets wrong
        // rather than merely saturating its magnitude. Nudging it one raw unit off MinValue before any of the three
        // negations below changes a legitimate near-unit normal by nothing measurable and keeps every negation exact.
        var negatable = new FixedVector3(
            X: FixedQ4816.FromRawBits(value: NudgeOffMinValue(raw: normal.X.Value)),
            Y: FixedQ4816.FromRawBits(value: NudgeOffMinValue(raw: normal.Y.Value)),
            Z: FixedQ4816.FromRawBits(value: NudgeOffMinValue(raw: normal.Z.Value))
        );

        tangent1 = (((absX <= absY) && (absX <= absZ))
            ? new FixedVector3(
                X: FixedQ4816.Zero,
                Y: negatable.Z,
                Z: -negatable.Y
            )
            : ((absY <= absZ)
                ? new FixedVector3(
                    X: -negatable.Z,
                    Y: FixedQ4816.Zero,
                    Z: negatable.X
                )
                : new FixedVector3(
                    X: negatable.Y,
                    Y: -negatable.X,
                    Z: FixedQ4816.Zero
                ))).Normalize();
        tangent2 = Cross(
            left: tangent1,
            right: normal
        );
    }
    /// <summary>Rounds each component to the nearest whole number, ties to even.</summary>
    /// <param name="value">The vector to round.</param>
    /// <returns>The vector of <see cref="FixedQ4816.Round"/> applied to each component.</returns>
    /// <exception cref="OverflowException">A rounded component exceeds <see cref="FixedQ4816.MaxValue"/>.</exception>
    public static FixedVector3 Round(FixedVector3 value) =>
        new(
            X: FixedQ4816.Round(value: value.X),
            Y: FixedQ4816.Round(value: value.Y),
            Z: FixedQ4816.Round(value: value.Z)
        );
    /// <summary>Converts to a single-precision <see cref="System.Numerics.Vector3"/> for presentation (the renderer).</summary>
    /// <returns>The nearest single-precision vector; precision may be lost for large magnitudes.</returns>
    public System.Numerics.Vector3 ToVector3() =>
        new(
            x: ((float)X),
            y: ((float)Y),
            z: ((float)Z)
        );
    /// <summary>Tries to get the full-width vector length.</summary>
    public bool TryLength(out FixedQ4816 length) =>
        FixedVectorMath.TryMagnitude(
            x: X.Value,
            y: Y.Value,
            z: Z.Value,
            result: out length
        );
    /// <summary>Tries to get the full-width squared vector length after one ties-to-even Q16 rounding.</summary>
    public bool TryLengthSquared(out FixedQ4816 squaredLength) =>
        FixedVectorMath.TrySquaredMagnitude(
            x: X.Value,
            y: Y.Value,
            z: Z.Value,
            result: out squaredLength
        );
}
