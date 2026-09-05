using System.Numerics;

namespace Puck.Maths;

/// <summary>
/// A quaternion of <see cref="FixedQ4816"/> components for deterministic 3D rotation: pure integer arithmetic, so
/// identical inputs produce identical bits on every machine. Rotation quaternions are unit quaternions — construct
/// with <see cref="FromAxisAngle"/>, compose with <c>*</c>, and renormalize with <see cref="Normalize"/> after long
/// composition chains (each component multiply rounds, so the norm drifts slowly). The vector part is the rotation
/// bivector (the oriented rotation plane, read as an axis); <see cref="Exp"/> and <see cref="Log"/> convert between
/// unit rotations and that half-angle-scaled bivector form. The generic-math interfaces expose the operator
/// capabilities required by <see cref="FixedDual{TValue}"/> (dual quaternions — see <see cref="FixedRigidTransform"/>).
/// Rounded fixed-point multiplication is not associative under bitwise equality, so these interfaces do not assert
/// that the type is a mathematical ring.
/// </summary>
/// <param name="X">The first vector component.</param>
/// <param name="Y">The second vector component.</param>
/// <param name="Z">The third vector component.</param>
/// <param name="W">The scalar component.</param>
public readonly record struct FixedQuaternion(FixedQ4816 X, FixedQ4816 Y, FixedQ4816 Z, FixedQ4816 W)
    : IAdditionOperators<FixedQuaternion, FixedQuaternion, FixedQuaternion>,
      ISubtractionOperators<FixedQuaternion, FixedQuaternion, FixedQuaternion>,
      IMultiplyOperators<FixedQuaternion, FixedQuaternion, FixedQuaternion>,
      IMultiplyOperators<FixedQuaternion, FixedQ4816, FixedQuaternion>,
      IUnaryNegationOperators<FixedQuaternion, FixedQuaternion>,
      IAdditiveIdentity<FixedQuaternion, FixedQuaternion>,
      IMultiplicativeIdentity<FixedQuaternion, FixedQuaternion> {
    // Below this candidate norm (2·cos(θ/2) ≈ within 0.45° of a half turn) FromTo's geometric-product construction
    // degenerates — the rotation plane is noise — and the perpendicular-axis fallback takes over.
    private static readonly FixedQ4816 AntiparallelThreshold = FixedQ4816.FromRawBits(value: 512L);
    // Above this cosine the interpolation angle is too small for a stable sine ratio; Slerp falls back to a
    // normalized linear blend.
    private static readonly FixedQ4816 NlerpThreshold = FixedQ4816.FromRawBits(value: 65503L);

    /// <summary>Gets the additive identity, the zero quaternion.</summary>
    public static FixedQuaternion AdditiveIdentity => default;
    /// <summary>Gets the identity rotation.</summary>
    public static FixedQuaternion Identity => new(
        X: FixedQ4816.Zero,
        Y: FixedQ4816.Zero,
        Z: FixedQ4816.Zero,
        W: FixedQ4816.One
    );
    /// <summary>Gets the full-width norm, saturating when it exceeds the scalar carrier.</summary>
    public FixedQ4816 Length => (TryLength(length: out var length)
        ? length
        : FixedQ4816.MaxValue
    );
    /// <summary>Gets the full-width squared norm rounded once to Q16, saturating when it exceeds the scalar carrier.</summary>
    public FixedQ4816 LengthSquared => (TryLengthSquared(squaredLength: out var squaredLength)
        ? squaredLength
        : FixedQ4816.MaxValue
    );
    /// <summary>Gets the multiplicative identity, the identity rotation.</summary>
    public static FixedQuaternion MultiplicativeIdentity => Identity;

    /// <summary>Adds two quaternions componentwise.</summary>
    /// <param name="left">The first addend.</param>
    /// <param name="right">The second addend.</param>
    /// <returns>The componentwise sum.</returns>
    public static FixedQuaternion operator +(FixedQuaternion left, FixedQuaternion right) =>
        new(
            X: (left.X + right.X),
            Y: (left.Y + right.Y),
            Z: (left.Z + right.Z),
            W: (left.W + right.W)
        );
    /// <summary>Subtracts <paramref name="right"/> from <paramref name="left"/> componentwise.</summary>
    /// <param name="left">The minuend.</param>
    /// <param name="right">The subtrahend.</param>
    /// <returns>The componentwise difference.</returns>
    public static FixedQuaternion operator -(FixedQuaternion left, FixedQuaternion right) =>
        new(
            X: (left.X - right.X),
            Y: (left.Y - right.Y),
            Z: (left.Z - right.Z),
            W: (left.W - right.W)
        );
    /// <summary>Scales a quaternion by a scalar.</summary>
    /// <param name="value">The quaternion to scale.</param>
    /// <param name="scalar">The scale factor.</param>
    /// <returns>The componentwise product.</returns>
    public static FixedQuaternion operator *(FixedQuaternion value, FixedQ4816 scalar) =>
        new(
            X: (value.X * scalar),
            Y: (value.Y * scalar),
            Z: (value.Z * scalar),
            W: (value.W * scalar)
        );
    /// <summary>Negates every component; <c>−q</c> represents the same rotation as <c>q</c>.</summary>
    /// <param name="value">The quaternion to negate.</param>
    /// <returns>The componentwise negation.</returns>
    public static FixedQuaternion operator -(FixedQuaternion value) =>
        new(
            X: -value.X,
            Y: -value.Y,
            Z: -value.Z,
            W: -value.W
        );
    /// <summary>Composes two rotations (the Hamilton product); <c>left * right</c> applies <paramref name="right"/> first.</summary>
    /// <param name="left">The second rotation.</param>
    /// <param name="right">The first rotation.</param>
    /// <returns>The composed rotation.</returns>
    /// <remarks>Each component's four full-width products accumulate before one ties-to-even Q16 rounding.</remarks>
    public static FixedQuaternion operator *(FixedQuaternion left, FixedQuaternion right) {
        var (lx, ly, lz, lw) = (left.X.Value, left.Y.Value, left.Z.Value, left.W.Value);
        var (rx, ry, rz, rw) = (right.X.Value, right.Y.Value, right.Z.Value, right.W.Value);
        const ulong NarrowLimit = (1UL << 30);
        var combinedMagnitude = FixedVectorMath.RawMagnitude(value: lx) | FixedVectorMath.RawMagnitude(value: ly) |
                                 FixedVectorMath.RawMagnitude(value: lz) | FixedVectorMath.RawMagnitude(value: lw) |
                                 FixedVectorMath.RawMagnitude(value: rx) | FixedVectorMath.RawMagnitude(value: ry) |
                                 FixedVectorMath.RawMagnitude(value: rz) | FixedVectorMath.RawMagnitude(value: rw);
        long x;
        long y;
        long z;
        long w;

        if (combinedMagnitude < NarrowLimit) {
            x = FixedQ4816.RoundProductSum(productSum: unchecked(((((lw * rx) + (lx * rw)) + (ly * rz)) - (lz * ry))));
            y = FixedQ4816.RoundProductSum(productSum: unchecked(((((lw * ry) - (lx * rz)) + (ly * rw)) + (lz * rx))));
            z = FixedQ4816.RoundProductSum(productSum: unchecked(((((lw * rz) + (lx * ry)) - (ly * rx)) + (lz * rw))));
            w = FixedQ4816.RoundProductSum(productSum: unchecked(((((lw * rw) - (lx * rx)) - (ly * ry)) - (lz * rz))));
        } else {
            x = FixedQ4816.RoundProductSum(productSum: unchecked(((((((Int128)lw) * rx) + (((Int128)lx) * rw)) + (((Int128)ly) * rz)) - (((Int128)lz) * ry))));
            y = FixedQ4816.RoundProductSum(productSum: unchecked(((((((Int128)lw) * ry) - (((Int128)lx) * rz)) + (((Int128)ly) * rw)) + (((Int128)lz) * rx))));
            z = FixedQ4816.RoundProductSum(productSum: unchecked(((((((Int128)lw) * rz) + (((Int128)lx) * ry)) - (((Int128)ly) * rx)) + (((Int128)lz) * rw))));
            w = FixedQ4816.RoundProductSum(productSum: unchecked(((((((Int128)lw) * rw) - (((Int128)lx) * rx)) - (((Int128)ly) * ry)) - (((Int128)lz) * rz))));
        }

        return new(
            X: FixedQ4816.FromRawBits(value: x),
            Y: FixedQ4816.FromRawBits(value: y),
            Z: FixedQ4816.FromRawBits(value: z),
            W: FixedQ4816.FromRawBits(value: w)
        );
    }

    private static long LandRotorLane(Int128 value, int shift) {
        var negative = (value < Int128.Zero);

        return FusedArithmetic.ScaleProductSum(
            shift: shift,
            value: (negative, ((UInt128)(negative ? -value : value)))
        );
    }
    private static UInt128 MagnitudeOf(Int128 value) =>
        ((UInt128)((value < Int128.Zero) ? -value : value));
    private static ulong MaximumRawMagnitude(FixedVector3 vector) =>
        Math.Max(
            val1: Math.Max(
                val1: FusedArithmetic.RawMagnitude(value: vector.X.Value),
                val2: FusedArithmetic.RawMagnitude(value: vector.Y.Value)
            ),
            val2: FusedArithmetic.RawMagnitude(value: vector.Z.Value)
        );

    // Norm of a vector part at full precision, saturating only when the scalar carrier cannot represent it.
    internal static FixedQ4816 VectorNorm(long x, long y, long z) =>
        (FixedVectorMath.TryMagnitude(
            result: out var magnitude,
            x: x,
            y: y,
            z: z
        )
            ? magnitude
            : FixedQ4816.MaxValue
        );

    /// <summary>Returns the conjugate — the inverse rotation for a unit quaternion.</summary>
    /// <returns>The quaternion with the vector part negated.</returns>
    public FixedQuaternion Conjugate() =>
        new(
            X: -X,
            Y: -Y,
            Z: -Z,
            W: W
        );
    /// <summary>Gets the dot product of two quaternions.</summary>
    /// <param name="left">The first quaternion.</param>
    /// <param name="right">The second quaternion.</param>
    /// <returns>The scalar dot product (four products accumulated exactly, one rounding).</returns>
    public static FixedQ4816 Dot(FixedQuaternion left, FixedQuaternion right) {
        const ulong NarrowLimit = (1UL << 30);
        var combinedMagnitude = FixedVectorMath.RawMagnitude(value: left.X.Value) | FixedVectorMath.RawMagnitude(value: left.Y.Value) |
                                 FixedVectorMath.RawMagnitude(value: left.Z.Value) | FixedVectorMath.RawMagnitude(value: left.W.Value) |
                                 FixedVectorMath.RawMagnitude(value: right.X.Value) | FixedVectorMath.RawMagnitude(value: right.Y.Value) |
                                 FixedVectorMath.RawMagnitude(value: right.Z.Value) | FixedVectorMath.RawMagnitude(value: right.W.Value);

        if (combinedMagnitude < NarrowLimit) {
            return FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked(
                ((((left.X.Value * right.X.Value) + (left.Y.Value * right.Y.Value)) +
                (left.Z.Value * right.Z.Value)) + (left.W.Value * right.W.Value)))));
        }

        return FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked(
            ((((((Int128)left.X.Value) * right.X.Value) + (((Int128)left.Y.Value) * right.Y.Value)) +
            (((Int128)left.Z.Value) * right.Z.Value)) + (((Int128)left.W.Value) * right.W.Value)))));
    }
    /// <summary>Computes the exponential of a bivector — the unit rotation it generates.</summary>
    /// <param name="bivector">The rotation plane scaled by the half-angle; equivalently the rotation axis times
    /// θ/2 in fixed-point radians.</param>
    /// <returns>The unit rotation quaternion <c>(b̂·sin |b|, cos |b|)</c>; the zero bivector maps to
    /// <see cref="Identity"/>.</returns>
    /// <remarks>The exponential map works in the half-angle (Lie algebra) domain: <c>Exp(axis · (θ/2))</c> equals
    /// <see cref="FromAxisAngle"/><c>(axis, θ)</c>, and angular velocity ω integrates as
    /// <c>Exp(ω · (dt/2)) * q</c>. Magnitudes beyond π wrap through the turn-domain reduction, with the phase taken
    /// from the full 64-bit norm (a multi-component norm can exceed the signed carrier) and the axis normalized
    /// full-range, so precision is independent of the bivector's absolute scale. Inverse of
    /// <see cref="Log"/>.</remarks>
    public static FixedQuaternion Exp(FixedVector3 bivector) {
        // b̂·sin, not b·(sin/θ): a Q16 quotient sin/θ underflows once θ outgrows sin, collapsing the vector part.
        // One shared pass yields the unit axis and the norm at full 64-bit range — a multi-component norm can
        // exceed the signed Q48.16 carrier, so the phase reduces from the unsaturated magnitude.
        if (!FixedVectorMath.TryNormalizeWithMagnitude(
            x: bivector.X.Value,
            y: bivector.Y.Value,
            z: bivector.Z.Value,
            unitX: out var unitX,
            unitY: out var unitY,
            unitZ: out var unitZ,
            rawMagnitude: out var angle
        )) {
            return Identity;
        }

        var (sin, cos) = FixedQ4816.SinCosRaw(rawAngle: angle);

        return new(
            X: (FixedQ4816.FromRawBits(value: unitX) * sin),
            Y: (FixedQ4816.FromRawBits(value: unitY) * sin),
            Z: (FixedQ4816.FromRawBits(value: unitZ) * sin),
            W: cos
        );
    }
    /// <summary>Creates the rotation of <paramref name="angle"/> (fixed-point radians) about <paramref name="axis"/>.</summary>
    /// <param name="axis">The rotation axis; must be unit length (see <see cref="FixedVector3.Normalize"/>).</param>
    /// <param name="angle">The rotation angle in radians; positive angles rotate counterclockwise about the axis.</param>
    /// <returns>The unit rotation quaternion.</returns>
    public static FixedQuaternion FromAxisAngle(FixedVector3 axis, FixedQ4816 angle) {
        // The half angle is exact in the turn domain — one more bit of shift — so an odd raw angle loses nothing.
        var (sin, cos) = FixedQ4816.SinCosHalfAngle(angle: angle);

        return new(
            X: (axis.X * sin),
            Y: (axis.Y * sin),
            Z: (axis.Z * sin),
            W: cos
        );
    }
    /// <summary>Converts a single-precision <see cref="System.Numerics.Quaternion"/> componentwise into fixed point —
    /// the inbound counterpart of <see cref="ToQuaternion"/>, and the one door an authored or renderer-side rotation
    /// takes into the deterministic world, so the rounding a caller gets is not a per-caller decision (the same role
    /// <see cref="FixedVector3.FromVector3"/> plays for a direction).</summary>
    /// <param name="value">The rotation to convert.</param>
    /// <returns>The nearest fixed-point quaternion, each component rounded to nearest with ties to even by
    /// <see cref="FixedQ4816.FromDouble"/>. The result is not renormalized: a source already off the unit sphere stays
    /// off it, and the Q16 quantization of the components moves it further, so call <see cref="Normalize"/> whenever
    /// the rotation is about to be used — exactly as the single-precision callers do.</returns>
    public static FixedQuaternion FromQuaternion(System.Numerics.Quaternion value) =>
        new(
            X: FixedQ4816.FromDouble(value: value.X),
            Y: FixedQ4816.FromDouble(value: value.Y),
            Z: FixedQ4816.FromDouble(value: value.Z),
            W: FixedQ4816.FromDouble(value: value.W)
        );
    /// <summary>Creates the shortest-arc rotation taking the direction of <paramref name="from"/> to the direction
    /// of <paramref name="to"/>.</summary>
    /// <param name="from">The start direction; any non-zero magnitude (directions are normalized internally).</param>
    /// <param name="to">The end direction; any non-zero magnitude.</param>
    /// <returns>The unit rotation with <c>Rotate(from)</c> along <paramref name="to"/>; <see cref="Identity"/> when
    /// either vector is zero.</returns>
    /// <remarks>The geometric-product construction <c>(f̂ × t̂, 1 + f̂·t̂)</c>, normalized — normalization halves the
    /// full-angle rotor into the half-angle quaternion (see <see cref="FixedComplex.FromTo"/> for the planar case).
    /// Directions within ~0.45° of antiparallel (where the construction's norm <c>2·cos(θ/2)</c> vanishes) rotate π
    /// about a deterministic axis perpendicular to <paramref name="from"/>. A common full-range preconditioner keeps
    /// directional precision independent of the inputs' absolute scale.</remarks>
    public static FixedQuaternion FromTo(FixedVector3 from, FixedVector3 to) {
        if (
            ((from.X.Value | from.Y.Value | from.Z.Value) == 0L) ||
            ((to.X.Value | to.Y.Value | to.Z.Value) == 0L)
        ) {
            return Identity;
        }

        // Scale-free, as the planar FixedComplex.FromTo: each input is landed in [2^45, 2^46) by an exact (or
        // thirty-bits-below-the-grid rounded) shift rather than normalized to Q16, the rotor (f × t, |f||t| + f·t) is
        // formed from those raws at full width, landed once more into the same window, and normalized once. Rounding
        // the inputs to unit Q16 first would quantize a 179° rotor's axis to about a tenth of a degree.
        var fromShift = FixedVectorMath.DirectionShift(rawMagnitude: MaximumRawMagnitude(vector: from));
        var toShift = FixedVectorMath.DirectionShift(rawMagnitude: MaximumRawMagnitude(vector: to));
        var fx = FixedVectorMath.ScaleRaw(shift: fromShift, value: from.X.Value);
        var fy = FixedVectorMath.ScaleRaw(shift: fromShift, value: from.Y.Value);
        var fz = FixedVectorMath.ScaleRaw(shift: fromShift, value: from.Z.Value);
        var tx = FixedVectorMath.ScaleRaw(shift: toShift, value: to.X.Value);
        var ty = FixedVectorMath.ScaleRaw(shift: toShift, value: to.Y.Value);
        var tz = FixedVectorMath.ScaleRaw(shift: toShift, value: to.Z.Value);
        // Every landed raw is below 2^46, so each product is below 2^92 and each sum of three below 2^94.
        var crossX = ((((Int128)fy) * tz) - (((Int128)fz) * ty));
        var crossY = ((((Int128)fz) * tx) - (((Int128)fx) * tz));
        var crossZ = ((((Int128)fx) * ty) - (((Int128)fy) * tx));
        var dot = (((((Int128)fx) * tx) + (((Int128)fy) * ty)) + (((Int128)fz) * tz));
        var fromNorm = ((FusedArithmetic.SquareMagnitude(value: fx) + FusedArithmetic.SquareMagnitude(value: fy)) + FusedArithmetic.SquareMagnitude(value: fz)).SquareRoot();
        var toNorm = ((FusedArithmetic.SquareMagnitude(value: tx) + FusedArithmetic.SquareMagnitude(value: ty)) + FusedArithmetic.SquareMagnitude(value: tz)).SquareRoot();
        var normProduct = (((UInt128)fromNorm) * toNorm);
        var scalar = (((Int128)normProduct) + dot);
        var rotorMagnitude = UInt128.Max(
            x: UInt128.Max(
                x: MagnitudeOf(value: crossX),
                y: MagnitudeOf(value: crossY)
            ),
            y: UInt128.Max(
                x: MagnitudeOf(value: crossZ),
                y: MagnitudeOf(value: scalar)
            )
        );
        // Land the rotor's largest component in [2^45, 2^46), carrying |f||t| through the same shift so the
        // antiparallel test below compares like with like.
        var rotorShift = (46 - FusedArithmetic.BitLength(value: rotorMagnitude));
        var cx = LandRotorLane(shift: rotorShift, value: crossX);
        var cy = LandRotorLane(shift: rotorShift, value: crossY);
        var cz = LandRotorLane(shift: rotorShift, value: crossZ);
        var cw = LandRotorLane(shift: rotorShift, value: scalar);

        // The rotor's norm is 2·|f||t|·cos(θ/2); it counts as antiparallel below AntiparallelThreshold/One of |f||t|.
        // Carried through the rotor's own shift, |f||t| either fits a machine word — then the squared comparison runs
        // exactly — or exceeds 2^63 against a landed rotor below 2^47, which is antiparallel by a wide margin.
        var antiparallel = ((FusedArithmetic.BitLength(value: normProduct) + rotorShift) > 63);

        if (!antiparallel) {
            var landedNormProduct = ((ulong)FusedArithmetic.ScaleProductSum(
                shift: rotorShift,
                value: (false, normProduct)
            ));
            var rotorNormSquared = (((FusedArithmetic.SquareMagnitude(value: cx) + FusedArithmetic.SquareMagnitude(value: cy)) + FusedArithmetic.SquareMagnitude(value: cz)) + FusedArithmetic.SquareMagnitude(value: cw));

            antiparallel = ((rotorNormSquared * ((UInt128)((ulong)(FixedQ4816.One.Value * FixedQ4816.One.Value)))) < ((((UInt128)landedNormProduct) * landedNormProduct) * ((UInt128)((ulong)(AntiparallelThreshold.Value * AntiparallelThreshold.Value)))));
        }

        if (antiparallel) {
            // Antiparallel: π about f̂ × ê for the basis vector ê least aligned with f̂.
            FixedVector3.OrthonormalBasis(
                normal: from.Normalize(),
                tangent1: out var axis,
                tangent2: out _
            );

            return new(
                X: axis.X,
                Y: axis.Y,
                Z: axis.Z,
                W: FixedQ4816.Zero
            );
        }

        (cx, cy, cz, cw) = FixedVectorMath.Normalize(
            w: cw,
            x: cx,
            y: cy,
            z: cz
        );

        return new(
            X: FixedQ4816.FromRawBits(value: cx),
            Y: FixedQ4816.FromRawBits(value: cy),
            Z: FixedQ4816.FromRawBits(value: cz),
            W: FixedQ4816.FromRawBits(value: cw)
        );
    }
    /// <summary>Returns the multiplicative inverse; a zero quaternion inverts to <see cref="Identity"/>.</summary>
    /// <returns>The conjugate divided by the exact full-width squared norm, with each final component rounded once.
    /// An inverse smaller than half a raw Q16 unit quantizes to zero. When the four-square raw sum reaches the
    /// 128-bit carrier's ceiling the member returns the zero quaternion directly, and that is the rounding: at any
    /// such magnitude every exact inverse component is at most <c>2⁻³³</c> of a raw unit, so the early-out and the
    /// full computation agree lane for lane.</returns>
    public FixedQuaternion Inverse() {
        if ((X.Value | Y.Value | Z.Value | W.Value) == 0L) {
            return Identity;
        }

        var complete = FixedVectorMath.TrySumSquares(
            x: X.Value,
            y: Y.Value,
            z: Z.Value,
            w: W.Value,
            squaredSum: out var squaredSum
        );

        if (!complete) {
            return default;
        }

        return new(
            X: FixedQ4816.FromRawBits(value: unchecked(-FixedVectorMath.DivideBySquaredSum(
                value: X.Value,
                squaredSum: squaredSum
            ))),
            Y: FixedQ4816.FromRawBits(value: unchecked(-FixedVectorMath.DivideBySquaredSum(
                value: Y.Value,
                squaredSum: squaredSum
            ))),
            Z: FixedQ4816.FromRawBits(value: unchecked(-FixedVectorMath.DivideBySquaredSum(
                value: Z.Value,
                squaredSum: squaredSum
            ))),
            W: FixedQ4816.FromRawBits(value: FixedVectorMath.DivideBySquaredSum(
                value: W.Value,
                squaredSum: squaredSum
            ))
        );
    }
    /// <summary>Computes the logarithm — the bivector generating this rotation, which must be unit length.</summary>
    /// <returns>The rotation plane scaled by the half-angle in <c>[0, π]</c>; equivalently the rotation axis times
    /// θ/2 in fixed-point radians. A quaternion with no vector part maps to <see cref="FixedVector3.Zero"/> (for
    /// <c>W &lt; 0</c> the plane is genuinely undefined — the fixed-point "no direction" answer, mirroring
    /// <see cref="FixedVector3.Normalize"/>).</returns>
    /// <remarks>Inverse of <see cref="Exp"/>: <c>Exp(q.Log())</c> recovers <c>q</c> (not <c>−q</c>; the sign
    /// survives the round trip) except at the vector-free <c>W &lt; 0</c> pole.</remarks>
    public FixedVector3 Log() {
        var vectorLength = VectorNorm(
            x: X.Value,
            y: Y.Value,
            z: Z.Value
        );

        if (vectorLength == FixedQ4816.Zero) {
            return FixedVector3.Zero;
        }

        // Each lane is one ties-to-even rounding of X·θ/|v|: the Q32 product over the Q16 norm lifted by K, so
        // DivideProductSum's own ·2¹⁶ cancels — the same folded-fraction form FixedRigidTransform's logarithm uses.
        var angle = FixedQ4816.Atan2(
            y: vectorLength,
            x: W
        );
        var denominator = (((UInt128)((ulong)vectorLength.Value)) << FixedQ4816.FractionBitCount);

        return new(
            X: FixedQ4816.FromRawBits(value: FusedArithmetic.DivideProductSum(
                denominator: denominator,
                numerator: FusedArithmetic.Product(
                    left: X.Value,
                    right: angle.Value
                )
            )),
            Y: FixedQ4816.FromRawBits(value: FusedArithmetic.DivideProductSum(
                denominator: denominator,
                numerator: FusedArithmetic.Product(
                    left: Y.Value,
                    right: angle.Value
                )
            )),
            Z: FixedQ4816.FromRawBits(value: FusedArithmetic.DivideProductSum(
                denominator: denominator,
                numerator: FusedArithmetic.Product(
                    left: Z.Value,
                    right: angle.Value
                )
            ))
        );
    }
    /// <summary>Returns a Q16-accurate unit quaternion along the same direction at any representable input scale; a zero
    /// quaternion normalizes to <see cref="Identity"/>.</summary>
    /// <returns>The normalized quaternion.</returns>
    public FixedQuaternion Normalize() {
        var rawMagnitude = Math.Max(
            val1: Math.Max(
                val1: FixedVectorMath.RawMagnitude(value: X.Value),
                val2: FixedVectorMath.RawMagnitude(value: Y.Value)
            ),
            val2: Math.Max(
                val1: FixedVectorMath.RawMagnitude(value: Z.Value),
                val2: FixedVectorMath.RawMagnitude(value: W.Value)
            )
        );

        if (rawMagnitude == 0UL) {
            return Identity;
        }

        var (x, y, z, w) = FixedVectorMath.Normalize(
            x: X.Value,
            y: Y.Value,
            z: Z.Value,
            w: W.Value
        );

        return new(
            X: FixedQ4816.FromRawBits(value: x),
            Y: FixedQ4816.FromRawBits(value: y),
            Z: FixedQ4816.FromRawBits(value: z),
            W: FixedQ4816.FromRawBits(value: w)
        );
    }
    /// <summary>Rotates a vector by this quaternion, which must be unit length.</summary>
    /// <param name="vector">The vector to rotate.</param>
    /// <returns>The rotated vector.</returns>
    /// <remarks>Two fused stages — v' = v + 2·u×(u×v + w·v) — each accumulates full-width products before one
    /// ties-to-even Q16 rounding per component.</remarks>
    public FixedVector3 Rotate(FixedVector3 vector) {
        var (ux, uy, uz, w) = (X.Value, Y.Value, Z.Value, W.Value);
        var (vx, vy, vz) = (vector.X.Value, vector.Y.Value, vector.Z.Value);
        const ulong RotationLimit = (1UL << 17);
        const ulong VectorLimit = (1UL << 40);
        var narrow = (((FixedVectorMath.RawMagnitude(value: ux) | FixedVectorMath.RawMagnitude(value: uy) |
                        FixedVectorMath.RawMagnitude(value: uz) | FixedVectorMath.RawMagnitude(value: w)) < RotationLimit) &&
                      ((FixedVectorMath.RawMagnitude(value: vx) | FixedVectorMath.RawMagnitude(value: vy) |
                        FixedVectorMath.RawMagnitude(value: vz)) < VectorLimit));
        long tx;
        long ty;
        long tz;
        long dx;
        long dy;
        long dz;

        if (narrow) {
            tx = FixedQ4816.RoundProductSum(productSum: unchecked((((uy * vz) - (uz * vy)) + (w * vx))));
            ty = FixedQ4816.RoundProductSum(productSum: unchecked((((uz * vx) - (ux * vz)) + (w * vy))));
            tz = FixedQ4816.RoundProductSum(productSum: unchecked((((ux * vy) - (uy * vx)) + (w * vz))));
            dx = FixedQ4816.RoundProductSum(productSum: unchecked(((uy * tz) - (uz * ty))));
            dy = FixedQ4816.RoundProductSum(productSum: unchecked(((uz * tx) - (ux * tz))));
            dz = FixedQ4816.RoundProductSum(productSum: unchecked(((ux * ty) - (uy * tx))));
        } else {
            tx = FixedQ4816.RoundProductSum(productSum: unchecked((((((Int128)uy) * vz) - (((Int128)uz) * vy)) + (((Int128)w) * vx))));
            ty = FixedQ4816.RoundProductSum(productSum: unchecked((((((Int128)uz) * vx) - (((Int128)ux) * vz)) + (((Int128)w) * vy))));
            tz = FixedQ4816.RoundProductSum(productSum: unchecked((((((Int128)ux) * vy) - (((Int128)uy) * vx)) + (((Int128)w) * vz))));
            dx = FixedQ4816.RoundProductSum(productSum: unchecked(((((Int128)uy) * tz) - (((Int128)uz) * ty))));
            dy = FixedQ4816.RoundProductSum(productSum: unchecked(((((Int128)uz) * tx) - (((Int128)ux) * tz))));
            dz = FixedQ4816.RoundProductSum(productSum: unchecked(((((Int128)ux) * ty) - (((Int128)uy) * tx))));
        }

        return new(
            X: FixedQ4816.FromRawBits(value: unchecked((vx + (dx << 1)))),
            Y: FixedQ4816.FromRawBits(value: unchecked((vy + (dy << 1)))),
            Z: FixedQ4816.FromRawBits(value: unchecked((vz + (dz << 1))))
        );
    }
    /// <summary>Rotates a vector by the inverse of this unit quaternion.</summary>
    /// <param name="vector">The vector to rotate.</param>
    /// <returns>The vector rotated by the conjugate quaternion through the same fused kernel as <see cref="Rotate"/>.</returns>
    public FixedVector3 RotateInverse(FixedVector3 vector) =>
        Conjugate().Rotate(vector: vector);
    /// <summary>Interpolates along the shortest great-circle arc between two unit rotations.</summary>
    /// <param name="from">The rotation at <paramref name="amount"/> zero.</param>
    /// <param name="to">The rotation at <paramref name="amount"/> one.</param>
    /// <param name="amount">The interpolation parameter, expected in <c>[0, 1]</c>.</param>
    /// <returns>The interpolated rotation, normalized.</returns>
    public static FixedQuaternion Slerp(FixedQuaternion from, FixedQuaternion to, FixedQ4816 amount) {
        var dot = Dot(
            left: from,
            right: to
        );

        if (dot < FixedQ4816.Zero) {
            to = -to;
            dot = -dot;
        }

        if (dot > NlerpThreshold) {
            // Nearly parallel: normalized linear blend (the sine ratio is numerically unstable here).
            return new FixedQuaternion(
                X: (from.X + ((to.X - from.X) * amount)),
                Y: (from.Y + ((to.Y - from.Y) * amount)),
                Z: (from.Z + ((to.Z - from.Z) * amount)),
                W: (from.W + ((to.W - from.W) * amount))
            ).Normalize();
        }

        // One SinCos serves both weights: sin((1−t)θ)/sin θ = cos(tθ) − cos θ·sin(tθ)/sin θ, with cos θ = dot.
        // sin θ = √(1 − dot²) from the exact Q32 radicand 2³² − dot², rounded once by the tie-free nearest root; the
        // angle t·θ reaches SinCos as the exact Q32 product, never rounded to the Q16 grid first.
        var sinTheta = FixedVectorMath.RootOfSquaredSum(squaredSum: ((ulong)((1L << (2 * FixedQ4816.FractionBitCount)) - (dot.Value * dot.Value))));
        var theta = FixedQ4816.Atan2(
            x: dot,
            y: sinTheta
        );

        var (sinScaled, cosScaled) = FixedQ4816.SinCosQ32(angleQ32: (amount.Value * theta.Value));
        var toWeight = (sinScaled / sinTheta);
        var fromWeight = (cosScaled - (dot * toWeight));

        return new FixedQuaternion(
            X: ((from.X * fromWeight) + (to.X * toWeight)),
            Y: ((from.Y * fromWeight) + (to.Y * toWeight)),
            Z: ((from.Z * fromWeight) + (to.Z * toWeight)),
            W: ((from.W * fromWeight) + (to.W * toWeight))
        ).Normalize();
    }
    /// <summary>Converts to a single-precision <see cref="System.Numerics.Quaternion"/> for presentation (the renderer).</summary>
    /// <returns>The nearest single-precision quaternion.</returns>
    public System.Numerics.Quaternion ToQuaternion() =>
        new(
            x: ((float)((double)X)),
            y: ((float)((double)Y)),
            z: ((float)((double)Z)),
            w: ((float)((double)W))
        );
    /// <summary>Tries to get the full-width quaternion norm.</summary>
    public bool TryLength(out FixedQ4816 length) =>
        FixedVectorMath.TryMagnitude(
            x: X.Value,
            y: Y.Value,
            z: Z.Value,
            w: W.Value,
            result: out length
        );
    /// <summary>Tries to get the full-width squared quaternion norm after one ties-to-even Q16 rounding.</summary>
    public bool TryLengthSquared(out FixedQ4816 squaredLength) =>
        FixedVectorMath.TrySquaredMagnitude(
            x: X.Value,
            y: Y.Value,
            z: Z.Value,
            w: W.Value,
            result: out squaredLength
        );
}
