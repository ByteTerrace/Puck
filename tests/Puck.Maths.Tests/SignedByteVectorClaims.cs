using System.Numerics;
using System.Runtime.Intrinsics;

namespace Puck.Maths.Tests;

/// <summary>Claims over signed 8-bit vector dot products, SIMD acceleration, cosine similarity, and normalization.</summary>
internal static class SignedByteVectorClaims {
    public static string? DotVsOracle() {
        // Oracle: exact BigInteger accumulation of products
        static BigInteger BigDot(ReadOnlySpan<sbyte> a, ReadOnlySpan<sbyte> b) {
            BigInteger sum = 0;

            for (var i = 0; (i < a.Length); i++) {
                sum += (((long)a[i]) * ((long)b[i]));
            }
            return sum;
        }

        var rng = new Random(Seed: 42);

        // Test lengths from 0 through 1025
        for (var len = 0; (len <= 1025); len++) {
            var a = new sbyte[len];
            var b = new sbyte[len];

            // Pattern 1: random
            for (var i = 0; (i < len); i++) {
                a[i] = ((sbyte)rng.Next(maxValue: 128, minValue: -128));
                b[i] = ((sbyte)rng.Next(maxValue: 128, minValue: -128));
            }

            var expected = BigDot(a: a, b: b);
            var actual = SignedByteVectorFunctions.Dot(left: a, right: b);

            if (actual != ((long)expected)) {
                return $"Dot mismatch on random at length {len}: expected {expected}, got {actual}";
            }

            var selfActual = SignedByteVectorFunctions.SumOfSquares(components: a);
            var selfExpected = BigDot(a: a, b: a);

            if (selfActual != ((long)selfExpected)) {
                return $"SumOfSquares mismatch at length {len}: expected {selfExpected}, got {selfActual}";
            }

            // Pattern 2: all 127
            Array.Fill(array: a, value: ((sbyte)127));
            Array.Fill(array: b, value: ((sbyte)127));
            expected = BigDot(a: a, b: b);
            actual = SignedByteVectorFunctions.Dot(left: a, right: b);
            if (actual != ((long)expected)) {
                return $"Dot mismatch on all-127 at length {len}: expected {expected}, got {actual}";
            }

            // Pattern 3: all -127
            Array.Fill(array: a, value: ((sbyte)-127));
            Array.Fill(array: b, value: ((sbyte)-127));
            expected = BigDot(a: a, b: b);
            actual = SignedByteVectorFunctions.Dot(left: a, right: b);
            if (actual != ((long)expected)) {
                return $"Dot mismatch on all--127 at length {len}: expected {expected}, got {actual}";
            }

            // Pattern 4: alternating extremes
            for (var i = 0; (i < len); i++) {
                a[i] = (((i % 2) == 0) ? (sbyte)127 : (sbyte)-127);
                b[i] = (((i % 2) == 0) ? (sbyte)-127 : (sbyte)127);
            }
            expected = BigDot(a: a, b: b);
            actual = SignedByteVectorFunctions.Dot(left: a, right: b);
            if (actual != ((long)expected)) {
                return $"Dot mismatch on alternating at length {len}: expected {expected}, got {actual}";
            }

            // Pattern 5: -128 values
            for (var i = 0; (i < len); i++) {
                a[i] = ((sbyte)-128);
                b[i] = ((sbyte)1);
            }
            expected = BigDot(a: a, b: b);
            actual = SignedByteVectorFunctions.Dot(left: a, right: b);
            if (actual != ((long)expected)) {
                return $"Dot mismatch on -128 at length {len}: expected {expected}, got {actual}";
            }
        }

        // Test length 1,048,577 (2^20 + 1) to cross the 256-block SIMD fold boundary
        var largeLen = 1_048_577;
        var largeA = new sbyte[largeLen];
        var largeB = new sbyte[largeLen];

        for (var i = 0; (i < largeLen); i++) {
            largeA[i] = ((sbyte)((i % 7) - 3));
            largeB[i] = ((sbyte)((i % 11) - 5));
        }
        var largeExpected = BigDot(a: largeA, b: largeB);
        var largeActual = SignedByteVectorFunctions.Dot(left: largeA, right: largeB);

        if (largeActual != ((long)largeExpected)) {
            return $"Dot mismatch at length {largeLen}: expected {largeExpected}, got {largeActual}";
        }

        return null;
    }
    public static string? DotTiersVsScalarRung() {
        int[] lengths = [
            0, 1, 7, 8, 9, 15, 16, 17, 31, 32, 33, 63, 64, 65, 127, 128, 255, 256, 511, 512, 1024, 2049,
            4095, 4096, 4097, 8191, 8192, 8193, 16383, 16384, 16385, 65537
        ];

        foreach (var length in lengths) {
            var left = new sbyte[length];
            var right = new sbyte[length];

            for (var i = 0; (i < length); i++) {
                left[i] = ((sbyte)((((i * 37) + 13) % 255) - 127));
                right[i] = ((sbyte)((((i * 73) + 29) % 255) - 127));
            }

            var scalarExpected = SignedByteVectorFunctions.DotScalar(left: left, right: right);

            if (SignedByteVectorFunctions.IsDotTierSupported(tier: SignedByteVectorTier.Vector128)) {
                var v128Actual = SignedByteVectorFunctions.DotVector<VectorLanes128, Vector128<sbyte>, Vector128<short>, Vector128<int>>(left: left, right: right);

                if (v128Actual != scalarExpected) {
                    return $"DotVector<VectorLanes128> mismatch at length {length}: expected {scalarExpected}, got {v128Actual}";
                }
            }

            if (SignedByteVectorFunctions.IsDotTierSupported(tier: SignedByteVectorTier.Vector256)) {
                var v256Actual = SignedByteVectorFunctions.DotVector<VectorLanes256, Vector256<sbyte>, Vector256<short>, Vector256<int>>(left: left, right: right);

                if (v256Actual != scalarExpected) {
                    return $"DotVector<VectorLanes256> mismatch at length {length}: expected {scalarExpected}, got {v256Actual}";
                }
            }

            if (SignedByteVectorFunctions.IsDotTierSupported(tier: SignedByteVectorTier.Vector512)) {
                var v512Actual = SignedByteVectorFunctions.DotVector<VectorLanes512, Vector512<sbyte>, Vector512<short>, Vector512<int>>(left: left, right: right);

                if (v512Actual != scalarExpected) {
                    return $"DotVector<VectorLanes512> mismatch at length {length}: expected {scalarExpected}, got {v512Actual}";
                }
            }

            if (length >= 4096) {
                Array.Fill(array: left, value: ((sbyte)127));
                Array.Fill(array: right, value: ((sbyte)127));
                var extremeExpected = SignedByteVectorFunctions.DotScalar(left: left, right: right);

                if (SignedByteVectorFunctions.IsDotTierSupported(tier: SignedByteVectorTier.Vector128)) {
                    var v128Actual = SignedByteVectorFunctions.DotVector<VectorLanes128, Vector128<sbyte>, Vector128<short>, Vector128<int>>(left: left, right: right);

                    if (v128Actual != extremeExpected) {
                        return $"DotVector<VectorLanes128> extreme overflow mismatch at length {length}: expected {extremeExpected}, got {v128Actual}";
                    }
                }

                if (SignedByteVectorFunctions.IsDotTierSupported(tier: SignedByteVectorTier.Vector256)) {
                    var v256Actual = SignedByteVectorFunctions.DotVector<VectorLanes256, Vector256<sbyte>, Vector256<short>, Vector256<int>>(left: left, right: right);

                    if (v256Actual != extremeExpected) {
                        return $"DotVector<VectorLanes256> extreme overflow mismatch at length {length}: expected {extremeExpected}, got {v256Actual}";
                    }
                }

                if (SignedByteVectorFunctions.IsDotTierSupported(tier: SignedByteVectorTier.Vector512)) {
                    var v512Actual = SignedByteVectorFunctions.DotVector<VectorLanes512, Vector512<sbyte>, Vector512<short>, Vector512<int>>(left: left, right: right);

                    if (v512Actual != extremeExpected) {
                        return $"DotVector<VectorLanes512> extreme overflow mismatch at length {length}: expected {extremeExpected}, got {v512Actual}";
                    }
                }
            }
        }

        return null;
    }
    public static string? CosineVsOracle() {
        static long OracleCosineQ16(ReadOnlySpan<sbyte> a, ReadOnlySpan<sbyte> b) {
            BigInteger dot = 0;
            BigInteger sumA = 0;
            BigInteger sumB = 0;

            for (var i = 0; (i < a.Length); i++) {
                dot += (((long)a[i]) * ((long)b[i]));
                sumA += (((long)a[i]) * ((long)a[i]));
                sumB += (((long)b[i]) * ((long)b[i]));
            }

            if ((sumA == 0) || (sumB == 0)) {
                return 0L;
            }

            var product = (sumA * sumB);
            var scaledProduct = (product << 32);
            var r = Oracles.IntegerSquareRoot(value: scaledProduct);

            if (r == 0) {
                return 0L;
            }

            var absDot = BigInteger.Abs(value: dot);
            var num = (absDot << 32);
            var quotient = (num / r);
            var rem = (num % r);
            var distToNext = (r - rem);

            BigInteger rounded;

            if (rem < distToNext) {
                rounded = quotient;
            } else if (rem > distToNext) {
                rounded = (quotient + 1);
            } else {
                rounded = (((quotient % 2) == 0) ? quotient : (quotient + 1));
            }

            var result = ((dot < 0) ? -rounded : rounded);

            if (result > 65536) {
                result = 65536;
            }
            if (result < -65536) {
                result = -65536;
            }
            return ((long)result);
        }


        var rng = new Random(Seed: 12345);
        int[] dimensions = [8, 16, 32, 64, 128, 256, 512, 1024];

        foreach (var dim in dimensions) {
            var a = new sbyte[dim];
            var b = new sbyte[dim];

            for (var i = 0; (i < dim); i++) {
                a[i] = ((sbyte)rng.Next(maxValue: 120, minValue: -120));
            }
            if (SignedByteVectorFunctions.CosineQ16(left: a, right: a) != 65536L) {
                return $"Self-cosine was not 65536 at dimension {dim}";
            }

            for (var i = 0; (i < dim); i++) {
                b[i] = ((sbyte)-a[i]);
            }
            if (SignedByteVectorFunctions.CosineQ16(left: a, right: b) != -65536L) {
                return $"Opposite cosine was not -65536 at dimension {dim}";
            }

            if (dim >= 2) {
                Array.Clear(array: a);
                Array.Clear(array: b);
                a[0] = 100;
                b[1] = 100;
                if (SignedByteVectorFunctions.CosineQ16(left: a, right: b) != 0L) {
                    return $"Orthogonal cosine was not 0 at dimension {dim}";
                }
            }

            for (var trial = 0; (trial < 100); trial++) {
                for (var i = 0; (i < dim); i++) {
                    a[i] = ((sbyte)rng.Next(maxValue: 128, minValue: -127));
                    b[i] = ((sbyte)rng.Next(maxValue: 128, minValue: -127));
                }

                var actual = SignedByteVectorFunctions.CosineQ16(left: a, right: b);
                var sym = SignedByteVectorFunctions.CosineQ16(left: b, right: a);

                if (actual != sym) {
                    return $"Cosine asymmetry at dimension {dim}: Cosine(a,b)={actual} != Cosine(b,a)={sym}";
                }

                if ((actual < -65536L) || (actual > 65536L)) {
                    return $"Cosine out of range at dimension {dim}: {actual}";
                }

                var oracle = OracleCosineQ16(a: a, b: b);

                if (actual != oracle) {
                    return $"Cosine deviation from oracle at dimension {dim}: actual={actual}, oracle={oracle}";
                }
            }
        }

        return null;
    }
    public static string? NormalizeAdmits() {

        static sbyte[] OracleNormalize(ReadOnlySpan<long> components) {
            BigInteger sumSquares = 0;

            for (var i = 0; (i < components.Length); i++) {
                var c = BigInteger.Abs(value: components[i]);

                sumSquares += (c * c);
            }
            if (sumSquares == 0) {
                return [];
            }
            var shiftedS = (sumSquares << 32);
            var r = Oracles.IntegerSquareRoot(value: shiftedS);

            if (r == 0) {
                return [];
            }
            var dest = new sbyte[components.Length];

            for (var i = 0; (i < components.Length); i++) {
                var c = components[i];
                var absC = BigInteger.Abs(value: c);
                var numerator = ((127 * absC) << 16);
                var quotient = (numerator / r);
                var remainder = (numerator % r);
                var distanceToNext = (r - remainder);
                BigInteger rounded;

                if (remainder < distanceToNext) {
                    rounded = quotient;
                } else if (remainder > distanceToNext) {
                    rounded = (quotient + 1);
                } else {
                    rounded = (((quotient % 2) == 0) ? quotient : (quotient + 1));
                }
                var sign = ((c < 0) ? -1 : ((c > 0) ? 1 : 0));
                var qi = ((long)(rounded * sign));

                dest[i] = ((sbyte)Math.Clamp(max: 127, min: -127, value: qi));
            }
            return dest;
        }

        static string? VerifyMatchesOracleAndAdmits(ReadOnlySpan<long> components, Span<sbyte> destination, string label) {
            if (!SignedByteVectorFunctions.TryNormalize(components: components, destination: destination)) {
                return $"TryNormalize failed for {label}";
            }
            if (!SignedByteVectorFunctions.IsUnitAdmissible(components: destination)) {
                return $"Result was not admissible for {label}";
            }
            var expected = OracleNormalize(components: components);

            if (!destination.SequenceEqual(other: expected)) {
                return $"Oracle mismatch for {label}";
            }
            return null;
        }

        var tie1Comps = new long[] { 1, 1, 1, 1, 0, 0, 0, 0 };
        var tie1Dest = new sbyte[8];

        if (VerifyMatchesOracleAndAdmits(components: tie1Comps, destination: tie1Dest, label: "exact-.5 tie odd->even") is { } errTie1) {
            return errTie1;
        }
        if (tie1Dest[0] != 64) {
            return $"Expected tie 63.5 to round to 64, got {tie1Dest[0]}";
        }

        var tie2Comps = new long[] { 5, 253, 21, 6, 2, 1, 0, 0 };
        var tie2Dest = new sbyte[8];

        if (VerifyMatchesOracleAndAdmits(components: tie2Comps, destination: tie2Dest, label: "exact-.5 tie even->even") is { } errTie2) {
            return errTie2;
        }
        if (tie2Dest[0] != 2) {
            return $"Expected tie 2.5 to round to 2, got {tie2Dest[0]}";
        }

        var rng = new Random(Seed: 99);
        int[] testDimensions = [8, 9, 255, 256, 1000, 1024];

        foreach (var n in testDimensions) {
            var components = new long[n];
            var destination = new sbyte[n];

            for (var i = 0; (i < n); i++) {
                components[i] = 100L;
            }
            if (VerifyMatchesOracleAndAdmits(components: components, destination: destination, label: $"uniform components at dim {n}") is { } errUniform) {
                return errUniform;
            }

            const long MaxMag = (1L << 24);

            for (var i = 0; (i < n); i++) {
                components[i] = (((i % 2) == 0) ? MaxMag : -MaxMag);
            }
            if (VerifyMatchesOracleAndAdmits(components: components, destination: destination, label: $"2^24 components at dim {n}") is { } errHigh) {
                return errHigh;
            }

            Array.Clear(array: components);
            components[0] = 5000L;
            if (VerifyMatchesOracleAndAdmits(components: components, destination: destination, label: $"one-hot vector at dim {n}") is { } errOneHot) {
                return errOneHot;
            }
            if (destination[0] != 127) {
                return $"Basis vector first component expected 127, got {destination[0]} at dim {n}";
            }

            for (var trial = 0; (trial < 10); trial++) {
                for (var i = 0; (i < n); i++) {
                    components[i] = (((long)((rng.NextDouble() * 2.0) * MaxMag)) - MaxMag);
                }
                if (VerifyMatchesOracleAndAdmits(components: components, destination: destination, label: $"random components trial {trial} at dim {n}") is { } errRand) {
                    return errRand;
                }
            }
        }

        var zeroComps = new long[8];
        var zeroDest = new sbyte[8];

        if (SignedByteVectorFunctions.TryNormalize(components: zeroComps, destination: zeroDest)) {
            return "TryNormalize succeeded unexpectedly for all-zero components";
        }

        var overComps = new long[8];

        overComps[0] = ((1L << 24) + 1L);
        try {
            SignedByteVectorFunctions.TryNormalize(components: overComps, destination: zeroDest);
            return "TryNormalize did not throw for component exceeding 2^24";
        } catch (ArgumentOutOfRangeException) {
        }

        return null;
    }
    public static string? QuantizeUnit() {
        int[] testDimensions = [8, 16, 256, 1024];

        foreach (var n in testDimensions) {
            var doubles = new double[n];
            var destination = new sbyte[n];

            // Unit vector on sphere
            var invNorm = (1.0 / Math.Sqrt(d: n));

            for (var i = 0; (i < n); i++) {
                doubles[i] = (((i % 2) == 0) ? invNorm : -invNorm);
            }

            if (!SignedByteVectorFunctions.TryQuantizeUnit(destination: destination, source: doubles)) {
                return $"TryQuantizeUnit failed for unit sphere vector at dimension {n}";
            }

            if (!SignedByteVectorFunctions.IsUnitAdmissible(components: destination)) {
                return $"Quantized unit vector was not admissible at dimension {n}";
            }

            // Test pinned basis vector: e_0 = [1.0, 0, ...] -> [127, 0, ...]
            Array.Clear(array: doubles);
            doubles[0] = 1.0;
            if (!SignedByteVectorFunctions.TryQuantizeUnit(destination: destination, source: doubles)) {
                return $"TryQuantizeUnit failed for basis vector at dimension {n}";
            }
            if (destination[0] != 127) {
                return $"Expected destination[0] == 127, got {destination[0]}";
            }
            for (var i = 1; (i < n); i++) {
                if (destination[i] != 0) {
                    return $"Expected destination[{i}] == 0, got {destination[i]}";
                }
            }
        }

        // Test refusals
        var refusalDoubles = new double[8];
        var refusalDest = new sbyte[8];

        // Zero
        if (SignedByteVectorFunctions.TryQuantizeUnit(destination: refusalDest, source: refusalDoubles)) {
            return "TryQuantizeUnit succeeded for zero vector";
        }

        // Non-finite
        refusalDoubles[0] = double.NaN;
        if (SignedByteVectorFunctions.TryQuantizeUnit(destination: refusalDest, source: refusalDoubles)) {
            return "TryQuantizeUnit succeeded for NaN component";
        }

        refusalDoubles[0] = double.PositiveInfinity;
        if (SignedByteVectorFunctions.TryQuantizeUnit(destination: refusalDest, source: refusalDoubles)) {
            return "TryQuantizeUnit succeeded for Infinity component";
        }

        // Magnitude > 8
        refusalDoubles[0] = 8.1;
        if (SignedByteVectorFunctions.TryQuantizeUnit(destination: refusalDest, source: refusalDoubles)) {
            return "TryQuantizeUnit succeeded for component magnitude > 8";
        }

        return null;
    }
    public static string? AdmissionTolerance() {
        for (var n = 1; (n <= 1024); n++) {
            var expected = (((int)Math.Ceiling(a: (Math.Sqrt(d: n) / 2.0))) + 1);
            var actual = SignedByteVectorFunctions.AdmissionTolerance(dimensions: n);

            if (actual != expected) {
                return $"AdmissionTolerance mismatch at n={n}: expected {expected}, got {actual}";
            }
        }

        try {
            _ = SignedByteVectorFunctions.AdmissionTolerance(dimensions: 0);
            return "AdmissionTolerance did not throw on n=0";
        } catch (ArgumentOutOfRangeException) {
            // Expected
        }

        try {
            _ = SignedByteVectorFunctions.AdmissionTolerance(dimensions: -5);
            return "AdmissionTolerance did not throw on n=-5";
        } catch (ArgumentOutOfRangeException) {
            // Expected
        }

        return null;
    }
}
