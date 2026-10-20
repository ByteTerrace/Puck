using System.Numerics;

namespace Puck.Maths.Tests;

/// <summary>Claims over <see cref="VectorFunctions"/>, the shared presentation-float helpers: admission checks, the
/// angle wraps and easings, the shortest arc and perpendicular, the −Z-forward attitude readbacks and the unorm
/// packers. Every subject here is floating point outside the determinism contract, so the laws pin exact bit
/// patterns where a value is exact, integer layouts where a value is packed, and bands where a value is
/// transcendental.</summary>
internal static class VectorFunctionsClaims {
    // A representative lane value from every finiteness class the IEEE-754 single format admits, plus a handful of
    // ordinary finite magnitudes; the full cross product over three lanes exercises every finite/non-finite mix a
    // Vector3 can carry without sweeping the carrier's whole 2³² lane space.
    private static readonly float[] LaneValues = [
        0f, -0f, 1f, -1f, 3.5f, -2.25f,
        float.Epsilon, -float.Epsilon,
        float.MinValue, float.MaxValue,
        float.PositiveInfinity, float.NegativeInfinity,
        float.NaN,
    ];

    public static string? IsFiniteVsOracle() {
        foreach (var x in LaneValues) {
            foreach (var y in LaneValues) {
                foreach (var z in LaneValues) {
                    var vector = new Vector3(x: x, y: y, z: z);
                    var expected = (float.IsFinite(f: x) && float.IsFinite(f: y) && float.IsFinite(f: z));
                    var actual = VectorFunctions.IsFinite(vector: vector);

                    if (actual != expected) {
                        return $"IsFinite({vector}) returned {actual}, but componentwise float.IsFinite says {expected}";
                    }

                    var planar = new Vector2(x: x, y: y);
                    var planarExpected = (float.IsFinite(f: x) && float.IsFinite(f: y));

                    if (VectorFunctions.IsFinite(vector: planar) != planarExpected) {
                        return $"IsFinite({planar}) disagrees with componentwise float.IsFinite";
                    }

                    foreach (var w in (ReadOnlySpan<float>)[1f, float.NaN, float.NegativeInfinity]) {
                        var wide = new Vector4(w: w, x: x, y: y, z: z);
                        var wideExpected = (expected && float.IsFinite(f: w));

                        if (VectorFunctions.IsFinite(vector: wide) != wideExpected) {
                            return $"IsFinite({wide}) disagrees with componentwise float.IsFinite";
                        }
                        if (VectorFunctions.IsFinite(rotation: new Quaternion(w: w, x: x, y: y, z: z)) != wideExpected) {
                            return $"IsFinite of the quaternion ({x}, {y}, {z}, {w}) disagrees with componentwise float.IsFinite";
                        }
                    }
                }
            }
        }

        return null;
    }
    /// <summary>WrapAngle lands in (−π, π] and WrapTurn in [0, 2π) over a ladder of angles in both precisions, each
    /// congruent to its input modulo 2π to within the input's own rounding, an angle already inside passes through
    /// exactly, −π maps to +π, and the non-finite inputs come back non-finite.</summary>
    public static string? AngleWrapsRangeAndCongruence() {
        foreach (var angle in (ReadOnlySpan<double>)[0d, 1d, -1d, 3d, -3d, Math.PI, -Math.PI, (2d * Math.PI), (-2d * Math.PI), 10d, -10d, 1e6d, -1e6d, 3.5d, -3.5d, 1e-9d, -1e-9d]) {
            if (CheckWraps(angle: angle) is { } doubleFailure) { return doubleFailure; }
            if (CheckWraps(angle: ((float)angle)) is { } singleFailure) { return singleFailure; }
        }

        if (VectorFunctions.WrapAngle(radians: -Math.PI) != Math.PI) { return "double WrapAngle(-pi) is not +pi"; }
        if (VectorFunctions.WrapAngle(radians: -MathF.PI) != MathF.PI) { return "single WrapAngle(-pi) is not +pi"; }
        if (!double.IsNaN(d: VectorFunctions.WrapAngle(radians: double.NaN))) { return "WrapAngle(NaN) is not NaN"; }
        if (!double.IsNaN(d: VectorFunctions.WrapTurn(radians: double.PositiveInfinity))) { return "WrapTurn(+inf) is not NaN"; }
        if (VectorFunctions.WrapTurn(radians: -1e-30f) != 0f) { return "a lift that rounds to a whole turn is not zero"; }

        return null;
    }
    private static string? CheckWraps<T>(T angle) where T : IFloatingPointIeee754<T> {
        var wrapped = VectorFunctions.WrapAngle(radians: angle);
        var turn = VectorFunctions.WrapTurn(radians: angle);
        var tolerance = (T.Abs(value: angle) * T.CreateTruncating(value: 1e-6));

        if ((wrapped <= -T.Pi) || (wrapped > T.Pi)) { return $"{typeof(T).Name} WrapAngle({angle}) = {wrapped} left (-pi, pi]"; }
        if ((turn < T.Zero) || (turn >= T.Tau)) { return $"{typeof(T).Name} WrapTurn({angle}) = {turn} left [0, 2pi)"; }

        var angleTurns = ((angle - wrapped) / T.Tau);
        var turnTurns = ((angle - turn) / T.Tau);

        if (T.Abs(value: (angleTurns - T.Round(x: angleTurns))) > (tolerance + T.CreateTruncating(value: 1e-6))) { return $"{typeof(T).Name} WrapAngle({angle}) is not congruent modulo 2pi"; }
        if (T.Abs(value: (turnTurns - T.Round(x: turnTurns))) > (tolerance + T.CreateTruncating(value: 1e-6))) { return $"{typeof(T).Name} WrapTurn({angle}) is not congruent modulo 2pi"; }
        if ((angle > -T.Pi) && (angle <= T.Pi) && (wrapped != angle)) { return $"{typeof(T).Name} WrapAngle moved {angle}, already inside"; }
        if ((angle >= T.Zero) && (angle < T.Tau) && (turn != angle)) { return $"{typeof(T).Name} WrapTurn moved {angle}, already inside"; }

        return null;
    }
    /// <summary>Smoothstep keeps the bits of the expression (t·t)·(3 − 2·t) in both precisions at a ladder of
    /// fractions, is exactly zero, one half and one at its three exact points, the edged form clamps and steps at equal
    /// edges, and Fract lands in [0, 1) with its complement an integer.</summary>
    public static string? EasingAndFract() {
        foreach (var fraction in (ReadOnlySpan<double>)[0d, 0.125d, 0.25d, 0.3d, 0.5d, 0.7d, 0.75d, 0.875d, 1d]) {
            var single = ((float)fraction);
            var expectedSingle = ((single * single) * (3f - (2f * single)));
            var expectedDouble = ((fraction * fraction) * (3d - (2d * fraction)));

            if (BitConverter.SingleToUInt32Bits(value: VectorFunctions.Smoothstep(t: single)) != BitConverter.SingleToUInt32Bits(value: expectedSingle)) { return $"single Smoothstep({single}) does not keep the expression's bits"; }
            if (BitConverter.DoubleToInt64Bits(value: VectorFunctions.Smoothstep(t: fraction)) != BitConverter.DoubleToInt64Bits(value: expectedDouble)) { return $"double Smoothstep({fraction}) does not keep the expression's bits"; }
            // The edged form's quotient is exact only at dyadic fractions, where the scaled span's difference is.
            if (((fraction * 8d) == Math.Floor(d: (fraction * 8d))) && (VectorFunctions.Smoothstep(edge0: 2d, edge1: 4d, x: (2d + (2d * fraction))) != VectorFunctions.Smoothstep(t: fraction))) { return $"the edged Smoothstep at {fraction} is not the unit form"; }
        }

        if (VectorFunctions.Smoothstep(t: 0f) != 0f) { return "Smoothstep(0) is not zero"; }
        if (VectorFunctions.Smoothstep(t: 0.5f) != 0.5f) { return "Smoothstep(1/2) is not a half"; }
        if (VectorFunctions.Smoothstep(t: 1f) != 1f) { return "Smoothstep(1) is not one"; }
        if (VectorFunctions.Smoothstep(edge0: 1f, edge1: 2f, x: -5f) != 0f) { return "the edged Smoothstep does not clamp below"; }
        if (VectorFunctions.Smoothstep(edge0: 1f, edge1: 2f, x: 7f) != 1f) { return "the edged Smoothstep does not clamp above"; }
        if (VectorFunctions.Smoothstep(edge0: 2f, edge1: 1f, x: 1.5f) != 0.5f) { return "reversed edges do not reverse the curve"; }
        if ((VectorFunctions.Smoothstep(edge0: 1f, edge1: 1f, x: 0.5f) != 0f) || (VectorFunctions.Smoothstep(edge0: 1f, edge1: 1f, x: 1f) != 1f)) { return "equal edges are not a step"; }

        foreach (var value in (ReadOnlySpan<double>)[0d, 0.25d, -0.25d, 1.75d, -1.75d, 123456.5d, -0.1d, 1e-9d]) {
            var fract = VectorFunctions.Fract(x: value);

            if ((fract < 0d) || (fract >= 1d)) { return $"Fract({value}) = {fract} left [0, 1)"; }
            if (Math.Floor(d: (value - fract)) != (value - fract)) { return $"Fract({value}) leaves a non-integer complement"; }
            if (VectorFunctions.Fract(x: ((float)value)) != ((float)value - MathF.Floor(x: ((float)value)))) { return $"single Fract({value}) is not x - floor(x)"; }
        }

        return null;
    }
    /// <summary>ShortestArc rotates its normalized start onto its normalized end within 1e-5 over a ladder of pairs,
    /// is the identity for aligned, zero and non-finite inputs, is a half turn about Perpendicular for antiparallel
    /// ones; Perpendicular is unit, orthogonal to its direction, built on the least-aligned axis, and UnitY for a
    /// direction with no perpendicular.</summary>
    public static string? ShortestArcAndPerpendicular() {
        Vector3[] directions = [
            Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitX, -Vector3.UnitY, -Vector3.UnitZ,
            new(x: 1f, y: 1f, z: 1f), new(x: 3f, y: -2f, z: 0.5f), new(x: 0.01f, y: 100f, z: -0.02f), new(x: -7f, y: 7.0001f, z: 7f),
        ];

        foreach (var from in directions) {
            foreach (var to in directions) {
                var arc = VectorFunctions.ShortestArc(
                    from: from,
                    to: to
                );
                var start = Vector3.Normalize(value: from);
                var end = Vector3.Normalize(value: to);
                var rotated = Vector3.Transform(
                    rotation: arc,
                    value: start
                );
                // Inside the antiparallel band the arc is a half turn, which carries the start to within the pair's
                // own sine of the end rather than onto it.
                var band = ((Vector3.Dot(vector1: start, vector2: end) <= (-1f + 1e-6f))
                    ? Vector3.Cross(vector1: start, vector2: end).Length()
                    : 0f);

                if (Vector3.Distance(value1: rotated, value2: end) > (1e-5f + band)) { return $"ShortestArc({from}, {to}) rotates to {rotated}"; }
                if (MathF.Abs(x: (arc.Length() - 1f)) > 1e-5f) { return $"ShortestArc({from}, {to}) is not unit"; }
            }

            var perpendicular = VectorFunctions.Perpendicular(direction: from);
            var absolute = Vector3.Abs(value: from);
            var least = (((absolute.X <= absolute.Y) && (absolute.X <= absolute.Z))
                ? Vector3.UnitX
                : ((absolute.Y <= absolute.Z)
                    ? Vector3.UnitY
                    : Vector3.UnitZ));

            if (MathF.Abs(x: (perpendicular.Length() - 1f)) > 1e-6f) { return $"Perpendicular({from}) is not unit"; }
            if (MathF.Abs(x: Vector3.Dot(vector1: perpendicular, vector2: from)) > (1e-5f * from.Length())) { return $"Perpendicular({from}) is not orthogonal"; }
            if (Vector3.Distance(value1: perpendicular, value2: Vector3.Normalize(value: Vector3.Cross(vector1: from, vector2: least))) > 1e-6f) { return $"Perpendicular({from}) is not built on the least-aligned axis"; }

            var half = VectorFunctions.ShortestArc(
                from: from,
                to: -from
            );
            var halfRotated = Vector3.Transform(
                rotation: half,
                value: Vector3.Normalize(value: from)
            );

            if (Vector3.Distance(value1: halfRotated, value2: -Vector3.Normalize(value: from)) > 1e-5f) { return $"the antiparallel arc of {from} does not reverse it"; }
            if (MathF.Abs(x: half.W) > 1e-6f) { return $"the antiparallel arc of {from} is not a half turn"; }
            if (Vector3.Distance(value1: new Vector3(x: half.X, y: half.Y, z: half.Z), value2: VectorFunctions.Perpendicular(direction: Vector3.Normalize(value: from))) > 1e-5f) { return $"the antiparallel arc of {from} is not about Perpendicular"; }
            if (VectorFunctions.ShortestArc(from: from, to: (from * 3f)) != Quaternion.Identity) { return $"the aligned arc of {from} is not the identity"; }
        }

        if (VectorFunctions.ShortestArc(from: Vector3.Zero, to: Vector3.UnitX) != Quaternion.Identity) { return "a zero start is not the identity"; }
        if (VectorFunctions.ShortestArc(from: Vector3.UnitX, to: Vector3.Zero) != Quaternion.Identity) { return "a zero end is not the identity"; }
        if (VectorFunctions.ShortestArc(from: new Vector3(x: float.NaN, y: 0f, z: 0f), to: Vector3.UnitX) != Quaternion.Identity) { return "a NaN start is not the identity"; }
        if (VectorFunctions.Perpendicular(direction: Vector3.Zero) != Vector3.UnitY) { return "a zero direction's perpendicular is not UnitY"; }
        if (VectorFunctions.Perpendicular(direction: new Vector3(x: float.PositiveInfinity, y: 0f, z: 0f)) != Vector3.UnitY) { return "a non-finite direction's perpendicular is not UnitY"; }

        return null;
    }
    /// <summary>The −Z-forward readbacks: the cardinal facings read as the convention says, Heading inverts YawOf and
    /// PitchOf within 1e-5 over a ladder, YawOf of an attitude is the yaw of its rotated −Z, and EulerOf recovers the
    /// yaw, pitch and roll an Ry·Rx·Rz attitude was built from within 1e-4.</summary>
    public static string? AttitudeReadbacks() {
        if (VectorFunctions.YawOf(facing: -Vector3.UnitZ) != 0f) { return "facing -Z is not yaw zero"; }
        if (MathF.Abs(x: (VectorFunctions.YawOf(facing: -Vector3.UnitX) - (MathF.PI / 2f))) > 1e-6f) { return "facing -X is not a left quarter turn"; }
        if (MathF.Abs(x: (VectorFunctions.YawOf(facing: Vector3.UnitX) + (MathF.PI / 2f))) > 1e-6f) { return "facing +X is not a right quarter turn"; }
        if (MathF.Abs(x: (VectorFunctions.YawOf(facing: Vector3.UnitZ) - MathF.PI)) > 1e-6f) { return "facing +Z is not a half turn"; }
        if (MathF.Abs(x: (VectorFunctions.PitchOf(facing: Vector3.UnitY) - (MathF.PI / 2f))) > 1e-6f) { return "facing +Y is not a pitch of a quarter turn"; }
        if (VectorFunctions.PitchOf(facing: -Vector3.UnitZ) != 0f) { return "facing -Z is not pitch zero"; }
        if (VectorFunctions.YawOf(attitude: Quaternion.Identity) != 0f) { return "the identity attitude is not yaw zero"; }

        foreach (var yaw in (ReadOnlySpan<float>)[0f, 0.5f, -0.5f, 1.5f, -1.5f, 3f, -3f, 2.2f]) {
            foreach (var pitch in (ReadOnlySpan<float>)[0f, 0.3f, -0.3f, 1.2f, -1.2f]) {
                var heading = VectorFunctions.Heading(
                    pitch: pitch,
                    yaw: yaw
                );
                var expectedHeading = new Vector3(
                    x: (-MathF.Sin(x: yaw) * MathF.Cos(x: pitch)),
                    y: MathF.Sin(x: pitch),
                    z: (-MathF.Cos(x: yaw) * MathF.Cos(x: pitch))
                );

                if (heading != expectedHeading) { return $"Heading({yaw}, {pitch}) does not keep the expression's bits"; }
                if (MathF.Abs(x: (heading.Length() - 1f)) > 1e-6f) { return $"Heading({yaw}, {pitch}) is not unit"; }
                if (MathF.Abs(x: VectorFunctions.WrapAngle(radians: (VectorFunctions.YawOf(facing: heading) - yaw))) > 1e-5f) { return $"YawOf(Heading({yaw}, {pitch})) does not recover the yaw"; }
                if (MathF.Abs(x: (VectorFunctions.PitchOf(facing: heading) - pitch)) > 1e-5f) { return $"PitchOf(Heading({yaw}, {pitch})) does not recover the pitch"; }

                foreach (var roll in (ReadOnlySpan<float>)[0f, 0.4f, -0.4f, 1.1f]) {
                    var attitude = Quaternion.Normalize(value: ((Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: yaw) * Quaternion.CreateFromAxisAngle(axis: Vector3.UnitX, angle: pitch)) * Quaternion.CreateFromAxisAngle(axis: Vector3.UnitZ, angle: roll)));
                    var (readYaw, readPitch, readRoll) = VectorFunctions.EulerOf(attitude: attitude);

                    if (MathF.Abs(x: VectorFunctions.WrapAngle(radians: (readYaw - yaw))) > 1e-4f) { return $"EulerOf does not recover yaw {yaw} (pitch {pitch}, roll {roll})"; }
                    if (MathF.Abs(x: (readPitch - pitch)) > 1e-4f) { return $"EulerOf does not recover pitch {pitch} (yaw {yaw}, roll {roll})"; }
                    if (MathF.Abs(x: VectorFunctions.WrapAngle(radians: (readRoll - roll))) > 1e-4f) { return $"EulerOf does not recover roll {roll} (yaw {yaw}, pitch {pitch})"; }
                    if (VectorFunctions.YawOf(attitude: attitude) != readYaw) { return "YawOf(attitude) is not EulerOf's yaw"; }
                    if (VectorFunctions.YawOf(attitude: attitude) != VectorFunctions.YawOf(facing: Vector3.Transform(rotation: attitude, value: -Vector3.UnitZ))) { return "YawOf(attitude) is not the yaw of its rotated -Z"; }
                }
            }
        }

        return null;
    }
    /// <summary>ToUnorm8 in both precisions is the ties-to-even rounding of the clamped value times 255, decided in
    /// decimal at exact ladder points including the ties; PackRgba8 and PackUnorm2x16 lay their lanes out low to
    /// high as the GPU decodes them; UnpackRgba8 inverts PackRgba8 on every byte value.</summary>
    public static string? UnormPackingVsExact() {
        // The expectation is the two roundings the member documents, both taken in arbitrary width: the exact rational
        // the clamped double denotes (its IEEE fields in BigInteger) times 255 is rounded to the nearest binary64 by
        // Oracles.NearestBinary64Bits, then that double's own exact rational is rounded ties-to-even to the code. For a
        // single-precision input the product is already exact, so the first rounding is the identity.
        static byte ExactUnorm8(double value) {
            var clamped = Math.Clamp(
                max: 1.0,
                min: 0.0,
                value: value
            );
            var (numerator, exponent) = Oracles.Binary64Rational(value: clamped);
            var productBits = Oracles.NearestBinary64Bits(
                shift: exponent,
                value: (numerator * 255)
            );
            var (productNumerator, productExponent) = Oracles.Binary64Rational(value: BitConverter.UInt64BitsToDouble(value: productBits));

            return ((byte)Oracles.RoundRationalTiesToEven(
                denominator: (BigInteger.One << productExponent),
                numerator: productNumerator
            ));
        }

        foreach (var value in (ReadOnlySpan<double>)[0d, 1d, 0.5d, 0.25d, (1d / 510d), (3d / 510d), (5d / 510d), (254.5d / 255d), 0.3d, 0.7d, 1e-9d, -1d, 2d, 1e300d, -0d]) {
            if (VectorFunctions.ToUnorm8(value: value) != ExactUnorm8(value: value)) { return $"ToUnorm8(double {value:R}) is not the exact rounding {ExactUnorm8(value: value)}"; }

            var single = ((float)value);

            if (VectorFunctions.ToUnorm8(value: single) != ExactUnorm8(value: single)) { return $"ToUnorm8(float {single:R}) is not the exact rounding {ExactUnorm8(value: single)}"; }
        }

        if (VectorFunctions.ToUnorm8(value: double.NaN) != 0) { return "ToUnorm8(double NaN) is not zero"; }
        if (VectorFunctions.ToUnorm8(value: float.NaN) != 0) { return "ToUnorm8(float NaN) is not zero"; }
        // 127.5 is exact in binary, so the tie goes to the even code 128; 0.5 in float likewise.
        if (VectorFunctions.ToUnorm8(value: 0.5d) != 128) { return "the half tie did not go to the even code"; }

        var packed = VectorFunctions.PackRgba8(rgba: new Vector4(w: 1f, x: (1f / 255f), y: (2f / 255f), z: (3f / 255f)));

        if (packed != 0xFF030201U) { return $"PackRgba8 laid out 0x{packed:X8}, not red in the low byte"; }

        for (var code = 0; (code < 256); ++code) {
            var word = ((uint)code) | (((uint)(255 - code)) << 8) | (((uint)((code * 7) & 0xFF)) << 16) | (((uint)((code * 13) & 0xFF)) << 24);
            var unpacked = VectorFunctions.UnpackRgba8(packed: word);

            if (unpacked.X != (code / 255f)) { return $"UnpackRgba8's red lane at {code} is not the byte over 255"; }
            if (unpacked.Y != ((255 - code) / 255f)) { return $"UnpackRgba8's green lane at {code} is not the byte over 255"; }
            if (unpacked.Z != (((code * 7) & 0xFF) / 255f)) { return $"UnpackRgba8's blue lane at {code} is not the byte over 255"; }
            if (unpacked.W != (((code * 13) & 0xFF) / 255f)) { return $"UnpackRgba8's alpha lane at {code} is not the byte over 255"; }
            if (VectorFunctions.PackRgba8(rgba: unpacked) != word) { return $"PackRgba8 does not invert UnpackRgba8 at 0x{word:X8}"; }
        }

        var uv = VectorFunctions.PackUnorm2x16(uv: new Vector2(x: (1f / 65535f), y: 1f));

        if (uv != 0xFFFF0001U) { return $"PackUnorm2x16 laid out 0x{uv:X8}, not u in the low half"; }
        if (VectorFunctions.PackUnorm2x16(uv: new Vector2(x: 0.5f, y: -1f)) != 32768U) { return "PackUnorm2x16 does not round 0.5 to 32768 and clamp below zero"; }
        if (VectorFunctions.PackUnorm2x16(uv: new Vector2(x: 2f, y: float.NaN)) != 0xFFFFU) { return "PackUnorm2x16 does not clamp above one and zero NaN"; }

        return null;
    }
}
