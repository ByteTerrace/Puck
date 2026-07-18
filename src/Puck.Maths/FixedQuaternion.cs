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
    private static readonly FixedQ4816 Half = FixedQ4816.FromRawBits(value: 32768L);
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
        var combinedMagnitude = (FixedVectorMath.RawMagnitude(value: lx) | FixedVectorMath.RawMagnitude(value: ly) |
                                 FixedVectorMath.RawMagnitude(value: lz) | FixedVectorMath.RawMagnitude(value: lw) |
                                 FixedVectorMath.RawMagnitude(value: rx) | FixedVectorMath.RawMagnitude(value: ry) |
                                 FixedVectorMath.RawMagnitude(value: rz) | FixedVectorMath.RawMagnitude(value: rw));
        long x;
        long y;
        long z;
        long w;

        if (combinedMagnitude < NarrowLimit) {
            x = FixedQ4816.RoundProductSum(productSum: unchecked((lw * rx) + (lx * rw) + (ly * rz) - (lz * ry)));
            y = FixedQ4816.RoundProductSum(productSum: unchecked((lw * ry) - (lx * rz) + (ly * rw) + (lz * rx)));
            z = FixedQ4816.RoundProductSum(productSum: unchecked((lw * rz) + (lx * ry) - (ly * rx) + (lz * rw)));
            w = FixedQ4816.RoundProductSum(productSum: unchecked((lw * rw) - (lx * rx) - (ly * ry) - (lz * rz)));
        } else {
            x = FixedQ4816.RoundProductSum(productSum: unchecked(((Int128)lw * rx) + ((Int128)lx * rw) + ((Int128)ly * rz) - ((Int128)lz * ry)));
            y = FixedQ4816.RoundProductSum(productSum: unchecked(((Int128)lw * ry) - ((Int128)lx * rz) + ((Int128)ly * rw) + ((Int128)lz * rx)));
            z = FixedQ4816.RoundProductSum(productSum: unchecked(((Int128)lw * rz) + ((Int128)lx * ry) - ((Int128)ly * rx) + ((Int128)lz * rw)));
            w = FixedQ4816.RoundProductSum(productSum: unchecked(((Int128)lw * rw) - ((Int128)lx * rx) - ((Int128)ly * ry) - ((Int128)lz * rz)));
        }

        return new(
            X: FixedQ4816.FromRawBits(value: x),
            Y: FixedQ4816.FromRawBits(value: y),
            Z: FixedQ4816.FromRawBits(value: z),
            W: FixedQ4816.FromRawBits(value: w)
        );
    }

    /// <summary>Gets the dot product of two quaternions.</summary>
    /// <param name="left">The first quaternion.</param>
    /// <param name="right">The second quaternion.</param>
    /// <returns>The scalar dot product (four products accumulated exactly, one rounding).</returns>
    public static FixedQ4816 Dot(FixedQuaternion left, FixedQuaternion right) {
        const ulong NarrowLimit = (1UL << 30);
        var combinedMagnitude = (FixedVectorMath.RawMagnitude(value: left.X.Value) | FixedVectorMath.RawMagnitude(value: left.Y.Value) |
                                 FixedVectorMath.RawMagnitude(value: left.Z.Value) | FixedVectorMath.RawMagnitude(value: left.W.Value) |
                                 FixedVectorMath.RawMagnitude(value: right.X.Value) | FixedVectorMath.RawMagnitude(value: right.Y.Value) |
                                 FixedVectorMath.RawMagnitude(value: right.Z.Value) | FixedVectorMath.RawMagnitude(value: right.W.Value));

        if (combinedMagnitude < NarrowLimit) {
            return FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked(
                (left.X.Value * right.X.Value) + (left.Y.Value * right.Y.Value) +
                (left.Z.Value * right.Z.Value) + (left.W.Value * right.W.Value))));
        }

        return FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked(
            ((Int128)left.X.Value * right.X.Value) + ((Int128)left.Y.Value * right.Y.Value) +
            ((Int128)left.Z.Value * right.Z.Value) + ((Int128)left.W.Value * right.W.Value))));
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
        var (sin, cos) = FixedQ4816.SinCos(angle: (angle * Half));

        return new(
            X: (axis.X * sin),
            Y: (axis.Y * sin),
            Z: (axis.Z * sin),
            W: cos
        );
    }
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
        var fromDirection = from.Normalize();
        var toDirection = to.Normalize();

        if ((fromDirection == FixedVector3.Zero) || (toDirection == FixedVector3.Zero)) {
            return Identity;
        }

        var (fx, fy, fz) = (fromDirection.X, fromDirection.Y, fromDirection.Z);
        var (tx, ty, tz) = (toDirection.X, toDirection.Y, toDirection.Z);
        // The full-angle rotor (f̂ × t̂, 1 + f̂·t̂), raw. Near antiparallel both parts vanish together, so normalize
        // through the exact 4-component norm — a rounded Q16 length-squared would erase the tiny candidate and
        // leave a non-unit result. Normalize bounds every raw component by 65,537: a cross sum is at most
        // 2·65,537² and the dot sum at most 3·65,537², both below 2³⁴, so these exact Q32 sums stay in long.
        // The rounded dot is below 2¹⁸ raw; adding raw One still leaves cw below 2¹⁹.
        var cx = FixedQ4816.RoundProductSum(productSum: unchecked((fy.Value * tz.Value) - (fz.Value * ty.Value)));
        var cy = FixedQ4816.RoundProductSum(productSum: unchecked((fz.Value * tx.Value) - (fx.Value * tz.Value)));
        var cz = FixedQ4816.RoundProductSum(productSum: unchecked((fx.Value * ty.Value) - (fy.Value * tx.Value)));
        var cw = unchecked((FixedQ4816.One.Value + FixedQ4816.RoundProductSum(productSum: unchecked(
            (fx.Value * tx.Value) + (fy.Value * ty.Value) + (fz.Value * tz.Value)))));
        var norm = FixedVectorMath.RootOfSquaredSum(squaredSum: unchecked((ulong)(((((cx * cx) + (cy * cy)) + (cz * cz)) + (cw * cw)))));

        if (norm < AntiparallelThreshold) {
            // Antiparallel: π about f̂ × ê for the basis vector ê least aligned with f̂.
            var absX = FixedQ4816.Abs(value: fx);
            var absY = FixedQ4816.Abs(value: fy);
            var absZ = FixedQ4816.Abs(value: fz);
            var axis = (((absX <= absY) && (absX <= absZ))
                ? new FixedVector3(X: FixedQ4816.Zero, Y: fz, Z: -fy)
                : ((absY <= absZ)
                    ? new FixedVector3(X: -fz, Y: FixedQ4816.Zero, Z: fx)
                    : new FixedVector3(X: fy, Y: -fx, Z: FixedQ4816.Zero))).Normalize();

            return new(
                X: axis.X,
                Y: axis.Y,
                Z: axis.Z,
                W: FixedQ4816.Zero
            );
        }

        (cx, cy, cz, cw) = FixedVectorMath.Normalize(x: cx, y: cy, z: cz, w: cw);

        return new(
            X: FixedQ4816.FromRawBits(value: cx),
            Y: FixedQ4816.FromRawBits(value: cy),
            Z: FixedQ4816.FromRawBits(value: cz),
            W: FixedQ4816.FromRawBits(value: cw)
        );
    }
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
        var sinTheta = FixedQ4816.Sqrt(value: (FixedQ4816.One - (dot * dot)));
        var theta = FixedQ4816.Atan2(
            y: sinTheta,
            x: dot
        );

        var (sinScaled, cosScaled) = FixedQ4816.SinCos(angle: (amount * theta));
        var toWeight = (sinScaled / sinTheta);
        var fromWeight = (cosScaled - (dot * toWeight));

        return new FixedQuaternion(
            X: ((from.X * fromWeight) + (to.X * toWeight)),
            Y: ((from.Y * fromWeight) + (to.Y * toWeight)),
            Z: ((from.Z * fromWeight) + (to.Z * toWeight)),
            W: ((from.W * fromWeight) + (to.W * toWeight))
        ).Normalize();
    }

    /// <summary>Gets the full-width squared norm rounded once to Q16, saturating when it exceeds the scalar carrier.</summary>
    public FixedQ4816 LengthSquared => (TryLengthSquared(out var squaredLength)
        ? squaredLength
        : FixedQ4816.MaxValue);
    /// <summary>Gets the full-width norm, saturating when it exceeds the scalar carrier.</summary>
    public FixedQ4816 Length => (TryLength(out var length)
        ? length
        : FixedQ4816.MaxValue);

    /// <summary>Tries to get the full-width quaternion norm.</summary>
    public bool TryLength(out FixedQ4816 length) =>
        FixedVectorMath.TryMagnitude(x: X.Value, y: Y.Value, z: Z.Value, w: W.Value, result: out length);

    /// <summary>Tries to get the full-width squared quaternion norm after one ties-to-even Q16 rounding.</summary>
    public bool TryLengthSquared(out FixedQ4816 squaredLength) =>
        FixedVectorMath.TrySquaredMagnitude(x: X.Value, y: Y.Value, z: Z.Value, w: W.Value, result: out squaredLength);

    /// <summary>Returns the conjugate — the inverse rotation for a unit quaternion.</summary>
    /// <returns>The quaternion with the vector part negated.</returns>
    public FixedQuaternion Conjugate() =>
        new(
        X: -X,
        Y: -Y,
        Z: -Z,
        W: W
    );
    /// <summary>Returns the multiplicative inverse; a zero quaternion inverts to <see cref="Identity"/>.</summary>
    /// <returns>The conjugate divided by the exact full-width squared norm, with each final component rounded once.
    /// An inverse smaller than half a raw Q16 unit quantizes to zero.</returns>
    public FixedQuaternion Inverse() {
        if ((X.Value | Y.Value | Z.Value | W.Value) == 0L) {
            return Identity;
        }

        var complete = FixedVectorMath.TrySumSquares(x: X.Value, y: Y.Value, z: Z.Value, w: W.Value, squaredSum: out var squaredSum);

        if (!complete) {
            return default;
        }

        return new(
            X: FixedQ4816.FromRawBits(value: unchecked(-FixedVectorMath.DivideBySquaredSum(value: X.Value, squaredSum: squaredSum))),
            Y: FixedQ4816.FromRawBits(value: unchecked(-FixedVectorMath.DivideBySquaredSum(value: Y.Value, squaredSum: squaredSum))),
            Z: FixedQ4816.FromRawBits(value: unchecked(-FixedVectorMath.DivideBySquaredSum(value: Z.Value, squaredSum: squaredSum))),
            W: FixedQ4816.FromRawBits(value: FixedVectorMath.DivideBySquaredSum(value: W.Value, squaredSum: squaredSum))
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

        var scale = (FixedQ4816.Atan2(y: vectorLength, x: W) / vectorLength);

        return new(
            X: (X * scale),
            Y: (Y * scale),
            Z: (Z * scale)
        );
    }
    /// <summary>Returns a Q16-accurate unit quaternion along the same direction at any representable input scale; a zero
    /// quaternion normalizes to <see cref="Identity"/>.</summary>
    /// <returns>The normalized quaternion.</returns>
    public FixedQuaternion Normalize() {
        var rawMagnitude = Math.Max(
            Math.Max(FixedVectorMath.RawMagnitude(value: X.Value), FixedVectorMath.RawMagnitude(value: Y.Value)),
            Math.Max(FixedVectorMath.RawMagnitude(value: Z.Value), FixedVectorMath.RawMagnitude(value: W.Value))
        );

        if (rawMagnitude == 0UL) {
            return Identity;
        }

        var (x, y, z, w) = FixedVectorMath.Normalize(x: X.Value, y: Y.Value, z: Z.Value, w: W.Value);

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
            tx = FixedQ4816.RoundProductSum(productSum: unchecked((uy * vz) - (uz * vy) + (w * vx)));
            ty = FixedQ4816.RoundProductSum(productSum: unchecked((uz * vx) - (ux * vz) + (w * vy)));
            tz = FixedQ4816.RoundProductSum(productSum: unchecked((ux * vy) - (uy * vx) + (w * vz)));
            dx = FixedQ4816.RoundProductSum(productSum: unchecked((uy * tz) - (uz * ty)));
            dy = FixedQ4816.RoundProductSum(productSum: unchecked((uz * tx) - (ux * tz)));
            dz = FixedQ4816.RoundProductSum(productSum: unchecked((ux * ty) - (uy * tx)));
        } else {
            tx = FixedQ4816.RoundProductSum(productSum: unchecked(((Int128)uy * vz) - ((Int128)uz * vy) + ((Int128)w * vx)));
            ty = FixedQ4816.RoundProductSum(productSum: unchecked(((Int128)uz * vx) - ((Int128)ux * vz) + ((Int128)w * vy)));
            tz = FixedQ4816.RoundProductSum(productSum: unchecked(((Int128)ux * vy) - ((Int128)uy * vx) + ((Int128)w * vz)));
            dx = FixedQ4816.RoundProductSum(productSum: unchecked(((Int128)uy * tz) - ((Int128)uz * ty)));
            dy = FixedQ4816.RoundProductSum(productSum: unchecked(((Int128)uz * tx) - ((Int128)ux * tz)));
            dz = FixedQ4816.RoundProductSum(productSum: unchecked(((Int128)ux * ty) - ((Int128)uy * tx)));
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
    // Norm of a vector part at full precision, saturating only when the scalar carrier cannot represent it.
    internal static FixedQ4816 VectorNorm(long x, long y, long z) =>
        (FixedVectorMath.TryMagnitude(x: x, y: y, z: z, result: out var magnitude)
            ? magnitude
            : FixedQ4816.MaxValue);

    /// <summary>Converts to a single-precision <see cref="System.Numerics.Quaternion"/> for presentation (the renderer).</summary>
    /// <returns>The nearest single-precision quaternion.</returns>
    public System.Numerics.Quaternion ToQuaternion() =>
        new(
        x: ((float)((double)X)),
        y: ((float)((double)Y)),
        z: ((float)((double)Z)),
        w: ((float)((double)W))
    );
}
