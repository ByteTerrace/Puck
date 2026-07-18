using System.Numerics;
using System.Runtime.Intrinsics.X86;

namespace Puck.Maths;

/// <summary>
/// A deterministic rigid transform (rotation plus translation) as a unit dual quaternion
/// (<see cref="FixedDual{TValue}"/> over <see cref="FixedQuaternion"/>): composition is the dual-quaternion
/// product, <see cref="Exp"/> and <see cref="Log"/> bridge to and from the generating screw (the dual bivector —
/// the rigid analog of <see cref="FixedQuaternion.Exp"/>), and <see cref="ScLerp"/> interpolates full rigid motions
/// along their screw axis the way <see cref="FixedQuaternion.Slerp"/> interpolates rotations. Pure integer arithmetic; identical inputs produce
/// identical bits on every machine. Keep instances unit-normalized (<see cref="Normalize"/>) after long
/// composition chains. Precision scales with translation magnitude (about 2⁻¹⁵ relative — the Q16 unit-quaternion
/// norm quantization): sub-millimeter at ten world units.
/// </summary>
/// <param name="Value">The underlying dual quaternion. Positional construction is an unchecked representation-level
/// operation; prefer <see cref="FromRotationTranslation"/> or <see cref="FromDualQuaternion"/> at trust boundaries.</param>
public readonly record struct FixedRigidTransform(FixedDual<FixedQuaternion> Value)
    : IMultiplyOperators<FixedRigidTransform, FixedRigidTransform, FixedRigidTransform>,
      IMultiplicativeIdentity<FixedRigidTransform, FixedRigidTransform> {
    private static readonly FixedQ4816 Half = FixedQ4816.FromRawBits(value: 32768L);
    // Above this relative-rotation cosine (‖vector part‖ < 512 raw, about 0.45°) the screw division amplifies
    // quantization; ScLerp falls back to a normalized linear blend, which is accurate for small rotations and exact
    // for pure translations. The vector norm rounds its exact raw Q32 sum to the nearest integer: at dot raw 65534
    // the complementary norm rounds to 512 (not blended), while at dot raw 65535 it rounds below 512 (blended).
    private static readonly FixedQ4816 BlendDotThreshold = FixedQ4816.FromRawBits(value: 65534L);

    /// <summary>Gets the identity transform.</summary>
    public static FixedRigidTransform Identity => new(Value: new(
        Real: FixedQuaternion.Identity,
        Dual: FixedQuaternion.AdditiveIdentity
    ));
    /// <summary>Gets the multiplicative identity, the transform that leaves points unchanged.</summary>
    public static FixedRigidTransform MultiplicativeIdentity => Identity;

    /// <summary>Composes two transforms; <c>left * right</c> applies <paramref name="right"/> first.</summary>
    /// <param name="left">The second transform.</param>
    /// <param name="right">The first transform.</param>
    /// <returns>The composed transform (the dual-quaternion product).</returns>
    /// <remarks>Composition is intentionally not normalized automatically: fixed-point normalization is comparatively
    /// expensive and changes rounding. Use <see cref="ComposeNormalized"/> at long-chain or untrusted boundaries.</remarks>
    public static FixedRigidTransform operator *(FixedRigidTransform left, FixedRigidTransform right) =>
        new(Value: (left.Value * right.Value));

    /// <summary>Composes two transforms and restores the unit rigid-dual-quaternion constraints.</summary>
    /// <param name="left">The second transform.</param>
    /// <param name="right">The first transform.</param>
    /// <returns>The normalized composition; <paramref name="right"/> is applied first.</returns>
    public static FixedRigidTransform ComposeNormalized(FixedRigidTransform left, FixedRigidTransform right) =>
        (left * right).Normalize();

    /// <summary>Creates a normalized rigid transform from a nondegenerate, rotation-scale dual quaternion.</summary>
    /// <param name="value">The dual quaternion to normalize.</param>
    /// <returns>The normalized rigid transform.</returns>
    /// <exception cref="ArgumentException">The real quaternion is zero.</exception>
    public static FixedRigidTransform FromDualQuaternion(FixedDual<FixedQuaternion> value) {
        if (!TryFromDualQuaternion(value: value, result: out var result)) {
            throw new ArgumentException(message: "A rigid transform requires a non-zero real quaternion.", paramName: nameof(value));
        }

        return result;
    }

    /// <summary>Attempts to normalize a rotation-scale dual quaternion into a rigid transform.</summary>
    /// <param name="value">The dual quaternion to normalize.</param>
    /// <param name="result">The normalized transform on success; otherwise <see cref="Identity"/>.</param>
    /// <returns><see langword="true"/> when the real quaternion is non-zero.</returns>
    public static bool TryFromDualQuaternion(FixedDual<FixedQuaternion> value, out FixedRigidTransform result) {
        if (!FixedVectorMath.TryCreateNormalizationScale(
            x: value.Real.X.Value,
            y: value.Real.Y.Value,
            z: value.Real.Z.Value,
            w: value.Real.W.Value,
            scale: out var scale
        )) {
            result = Identity;

            return false;
        }

        result = new FixedRigidTransform(Value: value).NormalizeCore(scale: scale);

        return true;
    }

    /// <summary>Creates the transform that rotates by <paramref name="rotation"/> and then translates by <paramref name="translation"/>.</summary>
    /// <param name="rotation">The rotation; normalized before it is encoded. A zero quaternion follows
    /// <see cref="FixedQuaternion.Normalize"/>'s identity convention.</param>
    /// <param name="translation">The translation, applied after the rotation.</param>
    /// <returns>The unit rigid transform <c>q + ε·½·t·q</c>.</returns>
    public static FixedRigidTransform FromRotationTranslation(FixedQuaternion rotation, FixedVector3 translation) {
        rotation = rotation.Normalize();
        var translationQuaternion = new FixedQuaternion(
            X: translation.X,
            Y: translation.Y,
            Z: translation.Z,
            W: FixedQ4816.Zero
        );

        return new(Value: new(
            Real: rotation,
            Dual: ((translationQuaternion * rotation) * Half)
        ));
    }
    /// <summary>Computes the exponential of a screw (a dual bivector) — the unit rigid transform it generates.</summary>
    /// <param name="real">The rotation bivector: the rotation plane scaled by the half-angle (exactly
    /// <see cref="FixedQuaternion.Exp"/>'s argument).</param>
    /// <param name="dual">The translational part: the screw moment scaled by the half-angle plus the axis scaled by
    /// the half-slide; for a pure translation <c>t</c>, exactly <c>t/2</c>.</param>
    /// <returns>The unit rigid transform; the zero screw maps to <see cref="Identity"/>.</returns>
    /// <remarks>Rigid velocity integrates as <c>Exp(ω·(dt/2), v·(dt/2)) * T</c>, and
    /// <see cref="ScLerp"/> is <c>from * Exp(amount · Log(from⁻¹·to))</c> — the same identity
    /// <see cref="FixedQuaternion.Slerp"/> has with <see cref="FixedQuaternion.Exp"/>. Inverse of
    /// <see cref="Log"/>. The dual part closes as <c>dual·(sin θ/θ) + û·(d/2)·(cos θ − sin θ/θ)</c> in wide fixed
    /// point with the half slide taken from the exact bivector·dual product, and stays within the family's
    /// ~2⁻¹⁵-relative-to-translation envelope (<see cref="ScLerp"/> also guards tiny rotations with its blend
    /// fallback).</remarks>
    public static FixedRigidTransform Exp(FixedVector3 real, FixedVector3 dual) {
        // û·sin, not real·(sin/θ): a Q16 quotient sin/θ underflows once θ outgrows sin, collapsing the rotation's
        // vector part. One shared pass yields the unit axis and the norm at full 64-bit range, so the phase never
        // saturates against the signed carrier.
        if (!FixedVectorMath.TryNormalizeWithMagnitude(
            x: real.X.Value,
            y: real.Y.Value,
            z: real.Z.Value,
            unitX: out var unitX,
            unitY: out var unitY,
            unitZ: out var unitZ,
            rawMagnitude: out var angle
        )) {
            return new(Value: new(
                Real: FixedQuaternion.Identity,
                Dual: new FixedQuaternion(
                    X: dual.X,
                    Y: dual.Y,
                    Z: dual.Z,
                    W: FixedQ4816.Zero
                )
            ));
        }

        var (sin, cos) = FixedQ4816.SinCosRaw(rawAngle: angle);
        // Half slide d/2 = û·dual, taken as (real·dual)/θ with an EXACT Int128 dot — a Q16-quantized axis dotted
        // against a large dual manufactures spurious slide (~|dual|·2⁻¹⁷). The closure dual·(sin/θ) + û·(d/2)·(cos −
        // sin/θ) is evaluated at Q63, where every product fits Int128, the small-θ cos − sin/θ difference keeps ~30
        // fractional bits, and a large θ cannot underflow the quotient against a full-magnitude dual. The
        // perpendicular part is never materialized in the Q16 carrier (its components can exceed it even when the
        // scaled result is representable).
        var halfSlide = DotOverAngle(real: real, dual: dual, angleRaw: angle);
        var scaleQ62 = SinOverAngleQ62(sinRaw: sin.Value, angleRaw: angle);
        // Both dual terms share the 2^78 product scale (raw·Q62 and rawSlide·Q32·Q46), so each component fuses into
        // ONE ties-even rounding; the summed Int128 magnitudes stay below 2^127.
        var diffQ46 = ((long)((((Int128)cos.Value) << 30) - (scaleQ62 >> 16)));
        var slideDiff = (halfSlide * diffQ46);

        return new(Value: new(
            Real: new(
                X: (FixedQ4816.FromRawBits(value: unitX) * sin),
                Y: (FixedQ4816.FromRawBits(value: unitY) * sin),
                Z: (FixedQ4816.FromRawBits(value: unitZ) * sin),
                W: cos
            ),
            Dual: new(
                X: FixedQ4816.FromRawBits(value: FixedQ4816.RoundProduct(
                    product: unchecked(((Int128)dual.X.Value * scaleQ62) + (unitX * slideDiff)),
                    fractionBitCount: 62)),
                Y: FixedQ4816.FromRawBits(value: FixedQ4816.RoundProduct(
                    product: unchecked(((Int128)dual.Y.Value * scaleQ62) + (unitY * slideDiff)),
                    fractionBitCount: 62)),
                Z: FixedQ4816.FromRawBits(value: FixedQ4816.RoundProduct(
                    product: unchecked(((Int128)dual.Z.Value * scaleQ62) + (unitZ * slideDiff)),
                    fractionBitCount: 62)),
                W: FixedQ4816.FromRawBits(value: unchecked(-FixedQ4816.RoundProductSum(productSum: (halfSlide * sin.Value))))
            )
        ));
    }

    // (real·dual)/θ at raw Q16: the exact Q32 dot divided by the raw angle, ties to even. This is û·dual with the
    // axis at full precision instead of Q16. Three signed 64×64 products can need 129 signed bits, so the wide
    // form accumulates positive and negative magnitudes separately; the quotient is bounded by ~|dual|
    // (Cauchy–Schwarz), which can exceed the signed carrier, so the half slide stays Int128.
    private static Int128 DotOverAngle(FixedVector3 real, FixedVector3 dual, ulong angleRaw) {
        const ulong NarrowLimit = (1UL << 30);
        var combinedMagnitude = (FixedVectorMath.RawMagnitude(value: real.X.Value) | FixedVectorMath.RawMagnitude(value: real.Y.Value) |
                                 FixedVectorMath.RawMagnitude(value: real.Z.Value) | FixedVectorMath.RawMagnitude(value: dual.X.Value) |
                                 FixedVectorMath.RawMagnitude(value: dual.Y.Value) | FixedVectorMath.RawMagnitude(value: dual.Z.Value));
        UInt128 magnitude;
        bool negative;

        if (combinedMagnitude < NarrowLimit) {
            // Six magnitudes below 2^30 keep the exact Q32 sum within a long.
            var dot = unchecked(
                ((real.X.Value * dual.X.Value) + (real.Y.Value * dual.Y.Value)) +
                (real.Z.Value * dual.Z.Value));
            var dotSign = (dot >> 63);

            magnitude = ((UInt128)((ulong)((dot ^ dotSign) - dotSign)));
            negative = (dotSign != 0L);
        } else {
            var positive = UInt128.Zero;
            var negativeSum = UInt128.Zero;
            var px = ((UInt128)FixedVectorMath.RawMagnitude(value: real.X.Value) * FixedVectorMath.RawMagnitude(value: dual.X.Value));
            var py = ((UInt128)FixedVectorMath.RawMagnitude(value: real.Y.Value) * FixedVectorMath.RawMagnitude(value: dual.Y.Value));
            var pz = ((UInt128)FixedVectorMath.RawMagnitude(value: real.Z.Value) * FixedVectorMath.RawMagnitude(value: dual.Z.Value));

            if ((real.X.Value ^ dual.X.Value) < 0L) { negativeSum += px; } else { positive += px; }
            if ((real.Y.Value ^ dual.Y.Value) < 0L) { negativeSum += py; } else { positive += py; }
            if ((real.Z.Value ^ dual.Z.Value) < 0L) { negativeSum += pz; } else { positive += pz; }

            if (positive >= negativeSum) {
                magnitude = (positive - negativeSum);
                negative = false;
            } else {
                magnitude = (negativeSum - positive);
                negative = true;
            }
        }

        var high = ((ulong)(magnitude >> 64));
        UInt128 quotient;
        ulong remainder;

        if (high == 0UL) {
            // Moderate operands: one hardware 64-bit division.
            var narrowQuotient = (((ulong)magnitude) / angleRaw);

            remainder = (((ulong)magnitude) - (narrowQuotient * angleRaw));
            quotient = narrowQuotient;
        } else if (X86Base.X64.IsSupported && (high < angleRaw)) {
#pragma warning disable SYSLIB5004
            var (wideQuotient, wideRemainder) = X86Base.X64.DivRem(lower: ((ulong)magnitude), upper: high, divisor: angleRaw);
#pragma warning restore SYSLIB5004

            quotient = wideQuotient;
            remainder = wideRemainder;
        } else {
            var wide = (magnitude / angleRaw);

            quotient = wide;
            remainder = ((ulong)(magnitude - (wide * angleRaw)));
        }

        var distanceToNext = (angleRaw - remainder);

        if ((remainder > distanceToNext) || ((remainder == distanceToNext) && ((quotient & UInt128.One) != UInt128.Zero))) {
            ++quotient;
        }

        return (negative
            ? (-((Int128)quotient))
            : ((Int128)quotient));
    }

    // sin θ / θ at Q62 (|sin| ≤ θ keeps the ratio within [−1, 1]), ties to even. |sin raw| ≤ angleRaw guarantees
    // the shifted numerator's high word is below the divisor, so the hardware 128-by-64 division always applies.
    private static Int128 SinOverAngleQ62(long sinRaw, ulong angleRaw) {
        var sign = (sinRaw >> 63);
        var magnitude = ((ulong)((sinRaw ^ sign) - sign));
        ulong quotient;
        ulong remainder;

        if (X86Base.X64.IsSupported) {
#pragma warning disable SYSLIB5004
            (quotient, remainder) = X86Base.X64.DivRem(lower: (magnitude << 62), upper: (magnitude >> 2), divisor: angleRaw);
#pragma warning restore SYSLIB5004
        } else {
            var numerator = (((UInt128)magnitude) << 62);
            var wide = (numerator / angleRaw);

            quotient = ((ulong)wide);
            remainder = ((ulong)(numerator - (wide * angleRaw)));
        }

        var distanceToNext = (angleRaw - remainder);

        if ((remainder > distanceToNext) || ((remainder == distanceToNext) && ((quotient & 1UL) != 0UL))) {
            ++quotient;
        }

        return ((sign == 0L)
            ? ((Int128)quotient)
            : (-((Int128)quotient)));
    }
    /// <summary>Interpolates along the screw axis between two unit transforms (screw linear interpolation),
    /// <c>from * Exp(amount · Log(from⁻¹ · to))</c>.</summary>
    /// <param name="from">The transform at <paramref name="amount"/> zero.</param>
    /// <param name="to">The transform at <paramref name="amount"/> one.</param>
    /// <param name="amount">The interpolation parameter, expected in <c>[0, 1]</c>.</param>
    /// <returns>The interpolated transform, normalized.</returns>
    public static FixedRigidTransform ScLerp(FixedRigidTransform from, FixedRigidTransform to, FixedQ4816 amount) {
        // Shortest path: a dual quaternion and its negation are the same transform.
        var target = to.Value;
        var dot = FixedQuaternion.Dot(
            left: from.Value.Real,
            right: target.Real
        );

        if (dot < FixedQ4816.Zero) {
            target = new(
                Real: -target.Real,
                Dual: -target.Dual
            );
            dot = -dot;
        }

        if (dot > BlendDotThreshold) {
            // Nearly rotation-free: normalized linear blend of the dual quaternions (Log's screw division would
            // amplify quantization ~1/sine here).
            return new FixedRigidTransform(Value: new(
                Real: (from.Value.Real + ((target.Real - from.Value.Real) * amount)),
                Dual: (from.Value.Dual + ((target.Dual - from.Value.Dual) * amount))
            )).Normalize();
        }

        var delta = new FixedRigidTransform(Value: (from.Inverse().Value * target));
        var sine = FixedQuaternion.VectorNorm(
            x: delta.Value.Real.X.Value,
            y: delta.Value.Real.Y.Value,
            z: delta.Value.Real.Z.Value
        );
        var (logReal, logDual) = delta.LogCore(sine: sine);

        return new FixedRigidTransform(Value: (from.Value * Exp(
            real: (logReal * amount),
            dual: (logDual * amount)
        ).Value)).Normalize();
    }

    /// <summary>Gets the rotation part.</summary>
    public FixedQuaternion Rotation => Value.Real;
    /// <summary>Gets the translation part, <c>2·dual·conj(real)</c>.</summary>
    public FixedVector3 Translation {
        get {
            var t = (Value.Dual * Value.Real.Conjugate());

            return new(
                X: (t.X + t.X),
                Y: (t.Y + t.Y),
                Z: (t.Z + t.Z)
            );
        }
    }

    /// <summary>Returns the inverse transform; valid for unit transforms.</summary>
    /// <returns>The transform with both quaternion parts conjugated.</returns>
    public FixedRigidTransform Inverse() =>
        new(Value: new(
        Real: Value.Real.Conjugate(),
        Dual: Value.Dual.Conjugate()
    ));
    /// <summary>Computes the logarithm — the screw (dual bivector) generating this transform, which must be
    /// unit.</summary>
    /// <returns><c>Real</c> = the rotation bivector (the axis times θ/2, matching
    /// <see cref="FixedQuaternion.Log"/>); <c>Dual</c> = the translational part (the screw moment times θ/2 plus the
    /// axis times the half-slide; for a pure translation <c>t</c>, exactly <c>t/2</c>). A rotation-free transform
    /// maps to <c>(Zero, translation/2)</c>; the vector-free <c>W &lt; 0</c> pole mirrors
    /// <see cref="FixedQuaternion.Log"/>.</returns>
    /// <remarks>Inverse of <see cref="Exp"/>. Near-zero rotations amplify quantization ~1/‖vector part‖ in the dual
    /// part (the screw division — the seam <see cref="ScLerp"/> guards with its blend fallback).</remarks>
    public (FixedVector3 Real, FixedVector3 Dual) Log() =>
        LogCore(sine: FixedQuaternion.VectorNorm(
            x: Value.Real.X.Value,
            y: Value.Real.Y.Value,
            z: Value.Real.Z.Value
        ));

    private (FixedVector3 Real, FixedVector3 Dual) LogCore(FixedQ4816 sine) {

        if (sine == FixedQ4816.Zero) {
            return (FixedVector3.Zero, new FixedVector3(
                X: Value.Dual.X,
                Y: Value.Dual.Y,
                Z: Value.Dual.Z
            ));
        }

        var halfAngle = FixedQ4816.Atan2(
            y: sine,
            x: Value.Real.W
        );
        var axisScale = (halfAngle / sine);
        // Dual part m·(θ/2) + û·(d/2) closes to Dv·(θ/2)/s + rv·(d/2)·(1 − w·(θ/2)/s)/s, with d/2 = −Dw/s.
        var halfSlide = (-Value.Dual.W / sine);
        var momentScale = ((halfSlide * (FixedQ4816.One - (Value.Real.W * axisScale))) / sine);

        return (
            new FixedVector3(
                X: (Value.Real.X * axisScale),
                Y: (Value.Real.Y * axisScale),
                Z: (Value.Real.Z * axisScale)
            ),
            new FixedVector3(
                X: ((Value.Dual.X * axisScale) + (Value.Real.X * momentScale)),
                Y: ((Value.Dual.Y * axisScale) + (Value.Real.Y * momentScale)),
                Z: ((Value.Dual.Z * axisScale) + (Value.Real.Z * momentScale))
            )
        );
    }
    /// <summary>Returns the unit-normalized transform: unit rotation, dual part re-orthogonalized against it.</summary>
    /// <returns>The normalized transform.</returns>
    public FixedRigidTransform Normalize() {
        if (!FixedVectorMath.TryCreateNormalizationScale(
            x: Value.Real.X.Value,
            y: Value.Real.Y.Value,
            z: Value.Real.Z.Value,
            w: Value.Real.W.Value,
            scale: out var scale
        )) {
            return Identity;
        }

        return NormalizeCore(scale: scale);
    }

    private FixedRigidTransform NormalizeCore(FixedVectorMath.NormalizationScale scale) {
        var real = new FixedQuaternion(
            X: FixedQ4816.FromRawBits(value: scale.Apply(value: Value.Real.X.Value)),
            Y: FixedQ4816.FromRawBits(value: scale.Apply(value: Value.Real.Y.Value)),
            Z: FixedQ4816.FromRawBits(value: scale.Apply(value: Value.Real.Z.Value)),
            W: FixedQ4816.FromRawBits(value: scale.Apply(value: Value.Real.W.Value))
        );
        var dual = new FixedQuaternion(
            X: FixedQ4816.FromRawBits(value: scale.Apply(value: Value.Dual.X.Value)),
            Y: FixedQ4816.FromRawBits(value: scale.Apply(value: Value.Dual.Y.Value)),
            Z: FixedQ4816.FromRawBits(value: scale.Apply(value: Value.Dual.Z.Value)),
            W: FixedQ4816.FromRawBits(value: scale.Apply(value: Value.Dual.W.Value))
        );

        // Enforce the unit constraint real·dual = 0 by projecting out the parallel component.
        return new(Value: new(
            Real: real,
            Dual: (dual - (real * FixedQuaternion.Dot(
                left: real,
                right: dual
            )))
        ));
    }
    /// <summary>Applies the transform to a point: rotate, then translate.</summary>
    /// <param name="point">The point to transform.</param>
    /// <returns>The transformed point.</returns>
    public FixedVector3 TransformPoint(FixedVector3 point) =>
        (Rotation.Rotate(vector: point) + Translation);
}
