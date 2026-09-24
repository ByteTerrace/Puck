using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Puck.Maths;

/// <summary>Mathematical operations on signed 8-bit vectors, including SIMD dot products and normalization.</summary>
public static class SignedByteVectorFunctions {
    /// <summary>Calculates the admission tolerance for unit vectors of the given dimension count.</summary>
    /// <param name="dimensions">The number of vector dimensions.</param>
    /// <returns>The integer tolerance bound around radius 127.</returns>
    public static int AdmissionTolerance(int dimensions) {
        if (dimensions <= 0) {
            throw new ArgumentOutOfRangeException(nameof(dimensions), "Dimensions must be positive.");
        }

        var floorSqrt = ((int)((uint)dimensions).SquareRoot());
        var ceilSqrt = (floorSqrt + (((floorSqrt * floorSqrt) < dimensions) ? 1 : 0));

        return (((ceilSqrt + 1) / 2) + 1);
    }
    /// <summary>Computes the exact integer dot product of two signed 8-bit vectors.</summary>
    /// <param name="left">The first vector.</param>
    /// <param name="right">The second vector.</param>
    /// <returns>The exact dot product without overflow or floating-point rounding.</returns>
    /// <exception cref="ArgumentException">Thrown when vector lengths differ.</exception>
    public static long Dot(ReadOnlySpan<sbyte> left, ReadOnlySpan<sbyte> right) {
        if (left.Length != right.Length) {
            throw new ArgumentException(message: "Vector lengths must match.", paramName: nameof(right));
        }

        if (Vector512.IsHardwareAccelerated && (left.Length >= Vector512<sbyte>.Count)) {
            return DotVector<VectorLanes512, Vector512<sbyte>, Vector512<short>, Vector512<int>>(left: left, right: right);
        }

        if (Vector256.IsHardwareAccelerated && (left.Length >= Vector256<sbyte>.Count)) {
            return DotVector<VectorLanes256, Vector256<sbyte>, Vector256<short>, Vector256<int>>(left: left, right: right);
        }

        if (Vector128.IsHardwareAccelerated && (left.Length >= Vector128<sbyte>.Count)) {
            return DotVector<VectorLanes128, Vector128<sbyte>, Vector128<short>, Vector128<int>>(left: left, right: right);
        }

        return DotScalar(left: left, right: right);
    }
    /// <summary>Computes the exact sum of squares (dot product with itself) of a signed 8-bit vector.</summary>
    /// <param name="components">The vector components.</param>
    /// <returns>The exact sum of squares.</returns>
    public static long SumOfSquares(ReadOnlySpan<sbyte> components) => Dot(left: components, right: components);
    /// <summary>Computes the exact cosine similarity between two signed 8-bit vectors in Q48.16 fixed-point format.</summary>
    /// <param name="left">The first vector.</param>
    /// <param name="right">The second vector.</param>
    /// <returns>The cosine similarity in Q48.16 (scaled by 65536, in [-65536, 65536]).</returns>
    public static long CosineQ16(ReadOnlySpan<sbyte> left, ReadOnlySpan<sbyte> right) {
        if (left.Length != right.Length) {
            throw new ArgumentException(message: "Vector lengths must match.", paramName: nameof(right));
        }

        var dot = Dot(left: left, right: right);
        var sumA = SumOfSquares(components: left);
        var sumB = SumOfSquares(components: right);

        if ((sumA == 0L) || (sumB == 0L)) {
            return 0L;
        }

        if (left.SequenceEqual(other: right) || ((sumA == sumB) && (dot == sumA))) {
            return 65536L;
        }

        var product = (((UInt128)((ulong)sumA)) * ((UInt128)((ulong)sumB)));
        var r = ((ulong)(product << 32).SquareRoot());

        if (r == 0UL) {
            return 0L;
        }

        var absDot = ((ulong)Math.Abs(value: dot));
        var numerator = (absDot << 32);
        var quotient = (numerator / r);
        var remainder = (numerator % r);
        var distanceToNext = (r - remainder);
        var rounded = FixedPointRounding.RoundHalfToEven(remainder: remainder, threshold: distanceToNext, truncated: quotient);
        var raw = ((dot < 0L) ? -((long)rounded) : (long)rounded);

        return Math.Clamp(max: 65536L, min: -65536L, value: raw);
    }

    /// <summary>Scalar reference rung for the signed 8-bit vector dot product.</summary>
    internal static long DotScalar(ReadOnlySpan<sbyte> left, ReadOnlySpan<sbyte> right) {
        if (left.Length != right.Length) {
            throw new ArgumentException(message: "Vector lengths must match.", paramName: nameof(right));
        }

        var sum = 0L;

        for (var i = 0; (i < left.Length); i++) {
            sum += (((long)left[i]) * right[i]);
        }

        return sum;
    }
    /// <summary>SIMD rung for the signed 8-bit vector dot product at one vector width.</summary>
    /// <typeparam name="TLanes">The vector width.</typeparam>
    /// <typeparam name="TSignedBytes">The eight-bit signed vector at that width.</typeparam>
    /// <typeparam name="TShorts">The sixteen-bit signed vector at that width.</typeparam>
    /// <typeparam name="TInts">The thirty-two-bit signed vector at that width.</typeparam>
    /// <param name="left">The first vector.</param>
    /// <param name="right">The second vector.</param>
    /// <returns>The exact dot product.</returns>
    /// <remarks>
    /// Each block adds four products of at most 2^14 in magnitude into every thirty-two-bit lane, so the lane
    /// accumulator is drained into the sixty-four-bit sum every 256 blocks, long before it could wrap.
    /// </remarks>
    /// <exception cref="ArgumentException">The vector lengths differ.</exception>
    internal static long DotVector<TLanes, TSignedBytes, TShorts, TInts>(ReadOnlySpan<sbyte> left, ReadOnlySpan<sbyte> right)
        where TLanes : ISignedByteWideningLanes<TSignedBytes, TShorts, TInts>
        where TSignedBytes : struct
        where TShorts : struct
        where TInts : struct {
        if (left.Length != right.Length) {
            throw new ArgumentException(message: "Vector lengths must match.", paramName: nameof(right));
        }

        ref var leftRef = ref MemoryMarshal.GetReference(span: left);
        ref var rightRef = ref MemoryMarshal.GetReference(span: right);
        var index = 0;
        var count = left.Length;
        var acc = default(TInts);
        var sum = 0L;
        var blockCount = 0;

        for (; ((index + TLanes.ByteCount) <= count); index += TLanes.ByteCount) {
            var vLeft = TLanes.Load(elementOffset: ((nuint)index), source: in leftRef);
            var vRight = TLanes.Load(elementOffset: ((nuint)index), source: in rightRef);

            var (lLow, lHigh) = TLanes.Widen(value: vLeft);
            var (rLow, rHigh) = TLanes.Widen(value: vRight);

            var prodHigh = TLanes.Multiply(left: lHigh, right: rHigh);
            var prodLow = TLanes.Multiply(left: lLow, right: rLow);

            var (p1, p2) = TLanes.Widen(value: prodLow);
            var (p3, p4) = TLanes.Widen(value: prodHigh);

            acc = TLanes.Add(left: acc, right: TLanes.Add(left: TLanes.Add(left: TLanes.Add(left: p1, right: p2), right: p3), right: p4));

            if (++blockCount == 256) {
                sum += TLanes.Sum(value: acc);
                acc = default;
                blockCount = 0;
            }
        }

        sum += TLanes.Sum(value: acc);

        for (; (index < count); index++) {
            sum += (((long)left[index]) * right[index]);
        }

        return sum;
    }
    /// <summary>Reports the highest hardware acceleration tier available on the current host.</summary>
    internal static SignedByteVectorTier GetHardwareTier() {
        if (Vector512.IsHardwareAccelerated) {
            return SignedByteVectorTier.Vector512;
        }

        if (Vector256.IsHardwareAccelerated) {
            return SignedByteVectorTier.Vector256;
        }

        if (Vector128.IsHardwareAccelerated) {
            return SignedByteVectorTier.Vector128;
        }

        return SignedByteVectorTier.Scalar;
    }
    /// <summary>Checks whether a given dot product hardware acceleration tier is supported.</summary>
    internal static bool IsDotTierSupported(SignedByteVectorTier tier) => tier switch {
        SignedByteVectorTier.Scalar => true,
        SignedByteVectorTier.Vector128 => Vector128.IsHardwareAccelerated,
        SignedByteVectorTier.Vector256 => Vector256.IsHardwareAccelerated,
        SignedByteVectorTier.Vector512 => Vector512.IsHardwareAccelerated,
        _ => false,
    };

    /// <summary>Determines whether the given components represent an admissible unit vector.</summary>
    /// <param name="components">The vector components.</param>
    /// <returns><see langword="true"/> if the components are in [-127, 127], not all zero, and within tolerance of radius 127; otherwise <see langword="false"/>.</returns>
    public static bool IsUnitAdmissible(ReadOnlySpan<sbyte> components) {
        if (components.Length == 0) {
            return false;
        }

        var sumSquares = 0UL;

        for (var i = 0; (i < components.Length); i++) {
            var c = components[i];

            if (c == -128) {
                return false;
            }

            sumSquares += ((ulong)(((long)c) * c));
        }

        if (sumSquares == 0UL) {
            return false;
        }

        var radiusFloor = ((int)sumSquares.SquareRoot());
        var tolerance = AdmissionTolerance(dimensions: components.Length);

        return (Math.Abs(value: (radiusFloor - 127)) <= tolerance);
    }
    /// <summary>Normalizes integer components to unit length on radius 127 using exact integer arithmetic.</summary>
    /// <param name="components">The source components, each with magnitude at most 2^24.</param>
    /// <param name="destination">The destination span to receive the normalized signed 8-bit components.</param>
    /// <returns><see langword="true"/> if normalization succeeded and produced an admissible vector; otherwise <see langword="false"/>.</returns>
    /// <remarks>
    /// <para>
    /// With S = sum(c_i^2) and r = floorSqrt(S * 2^32) = floor(sqrt(S) * 65536), each component is computed as
    /// q_i = sign(c_i) * roundHalfEven((127 * |c_i|) &lt;&lt; 16, r).
    /// </para>
    /// <para>
    /// The quotient error is below 0.5 + 127/65535, so for up to 1024 dimensions the error norm is at most
    /// sqrt(1024) * (0.5 + 127/65535) ~= 16.07, which is strictly bounded by tolerance(1024) = 17.
    /// The result is thus admitted by construction.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when any component exceeds 2^24 in magnitude.</exception>
    public static bool TryNormalize(ReadOnlySpan<long> components, Span<sbyte> destination) {
        if ((components.Length == 0) || (destination.Length != components.Length)) {
            return false;
        }

        const long MaxComponentMagnitude = (1L << 24);
        var sumSquares = 0UL;

        for (var i = 0; (i < components.Length); i++) {
            var c = components[i];
            var absC = Math.Abs(value: c);

            if (absC > MaxComponentMagnitude) {
                throw new ArgumentOutOfRangeException(nameof(components), "Component magnitude exceeds 2^24.");
            }

            sumSquares += ((ulong)(absC * absC));
        }

        if (sumSquares == 0UL) {
            return false;
        }

        var shiftedS = (((UInt128)sumSquares) << 32);
        var r = ((ulong)shiftedS.SquareRoot());

        if (r == 0UL) {
            return false;
        }

        for (var i = 0; (i < components.Length); i++) {
            var c = components[i];
            var absC = ((ulong)Math.Abs(value: c));
            var numerator = ((127UL * absC) << 16);
            var quotient = (numerator / r);
            var remainder = (numerator % r);
            var distanceToNext = (r - remainder);

            var rounded = FixedPointRounding.RoundToNearestTiesToEven(
                distanceToNext: distanceToNext,
                distanceToTruncated: remainder,
                truncated: quotient
            );

            var sign = ((c < 0) ? -1L : ((c > 0) ? 1L : 0L));
            var qi = (((long)rounded) * sign);

            destination[i] = ((sbyte)Math.Clamp(max: 127L, min: -127L, value: qi));
        }

        return IsUnitAdmissible(components: destination);
    }
    /// <summary>Quantizes a floating-point unit vector into signed 8-bit components on radius 127.</summary>
    /// <param name="source">The floating-point components, each finite with magnitude at most 8.</param>
    /// <param name="destination">The destination span to receive the normalized signed 8-bit components.</param>
    /// <returns><see langword="true"/> if quantization succeeded; otherwise <see langword="false"/>.</returns>
    public static bool TryQuantizeUnit(ReadOnlySpan<double> source, Span<sbyte> destination) {
        if ((source.Length == 0) || (destination.Length != source.Length)) {
            return false;
        }

        var components = ((source.Length <= 1024) ? stackalloc long[source.Length] : new long[source.Length]);

        for (var i = 0; (i < source.Length); i++) {
            var x = source[i];

            if (!double.IsFinite(d: x) || (Math.Abs(value: x) > 8.0)) {
                return false;
            }

            components[i] = ((long)Math.Round(mode: MidpointRounding.ToEven, value: (x * 1048576.0)));
        }

        return TryNormalize(components: components, destination: destination);
    }
}
