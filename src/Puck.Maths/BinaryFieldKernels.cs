using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Puck.Maths;

/// <summary>
/// Provides the arithmetic kernels beneath <see cref="BinaryField{T}"/>, including the carryless-multiply primitive in
/// both its hardware and its portable form.
/// </summary>
/// <remarks>
/// The two multiplication tiers are separately named and no portable member queries instruction-set support, so a
/// verifier can execute both tiers over the same inputs inside one process and compare them. Everything above the
/// carryless product — reduction, squaring, inversion, division, and exponentiation — is a single implementation
/// shared by both tiers, which makes their agreement above the product structural rather than tested.
/// </remarks>
internal static class BinaryFieldKernels {
    /// <summary>The number of whole vectors a region must span before a nibble-split rung is preferred to the scalar loop.</summary>
    /// <remarks>
    /// Building the two sixteen-entry tables costs about sixteen scalar field multiplies, which a region shorter than a
    /// few vectors never earns back. The threshold is a throughput tuning value and never a correctness bound: every
    /// rung produces the same bytes at every length, so moving it can only change how fast the answer arrives.
    /// </remarks>
    private const int SplitTableAmortizationVectors = 4;

    /// <summary>Adds one region of packed field elements into another, elementwise.</summary>
    /// <typeparam name="T">The packed element carrier.</typeparam>
    /// <param name="destination">The region to add into, whose length matches <paramref name="source"/>.</param>
    /// <param name="source">The region to add.</param>
    /// <remarks>
    /// Addition in characteristic two is the exclusive or at every degree, so region addition is one degree-independent
    /// byte-wise loop rather than one implementation per carrier. The vector width is chosen by hardware acceleration
    /// alone because the result is the same at every width.
    /// </remarks>
    internal static void AddRegion<T>(Span<T> destination, ReadOnlySpan<T> source) where T : IBinaryInteger<T>, IUnsignedNumber<T> {
        var count = (destination.Length * Unsafe.SizeOf<T>());
        var index = 0;
        ref var destinationBytes = ref Unsafe.As<T, byte>(source: ref MemoryMarshal.GetReference(span: destination));
        ref var sourceBytes = ref Unsafe.As<T, byte>(source: ref MemoryMarshal.GetReference(span: source));

        if (Vector512.IsHardwareAccelerated) {
            for (; ((index + 64) <= count); index += 64) {
                (Vector512.LoadUnsafe(source: ref destinationBytes, elementOffset: ((nuint)index)) ^
                 Vector512.LoadUnsafe(source: ref sourceBytes, elementOffset: ((nuint)index)))
                    .StoreUnsafe(destination: ref destinationBytes, elementOffset: ((nuint)index));
            }
        }

        if (Vector256.IsHardwareAccelerated) {
            for (; ((index + 32) <= count); index += 32) {
                (Vector256.LoadUnsafe(source: ref destinationBytes, elementOffset: ((nuint)index)) ^
                 Vector256.LoadUnsafe(source: ref sourceBytes, elementOffset: ((nuint)index)))
                    .StoreUnsafe(destination: ref destinationBytes, elementOffset: ((nuint)index));
            }
        }

        if (Vector128.IsHardwareAccelerated) {
            for (; ((index + 16) <= count); index += 16) {
                (Vector128.LoadUnsafe(source: ref destinationBytes, elementOffset: ((nuint)index)) ^
                 Vector128.LoadUnsafe(source: ref sourceBytes, elementOffset: ((nuint)index)))
                    .StoreUnsafe(destination: ref destinationBytes, elementOffset: ((nuint)index));
            }
        }

        for (; (index < count); ++index) {
            Unsafe.Add(source: ref destinationBytes, elementOffset: index) ^= Unsafe.Add(source: ref sourceBytes, elementOffset: index);
        }
    }
    /// <summary>Computes the exact carryless product of two polynomials packed into <see cref="ulong"/> values.</summary>
    /// <param name="left">The left operand; bit <c>i</c> is the coefficient of <c>t^i</c>.</param>
    /// <param name="right">The right operand; bit <c>i</c> is the coefficient of <c>t^i</c>.</param>
    /// <returns>The exact 128-bit product, split into its low and high 64-bit limbs.</returns>
    /// <remarks>
    /// The hardware carryless multiplication is used when its instruction set is available; otherwise the table-free
    /// masked-comb fallback produces the identical product.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static (ulong Low, ulong High) CarrylessMultiply64(ulong left, ulong right) {
        if (Pclmulqdq.IsSupported) { return CarrylessMultiply64Hardware(left: left, right: right); }

        return CarrylessMultiply64Portable(left: left, right: right);
    }
    /// <summary>Computes the exact carryless product of two polynomials packed into <see cref="ulong"/> values, using the hardware carryless-multiply instruction unconditionally.</summary>
    /// <param name="left">The left operand; bit <c>i</c> is the coefficient of <c>t^i</c>.</param>
    /// <param name="right">The right operand; bit <c>i</c> is the coefficient of <c>t^i</c>.</param>
    /// <returns>The exact 128-bit product, split into its low and high 64-bit limbs.</returns>
    /// <remarks>
    /// The limb-to-lane correspondence is stated in source rather than inherited from struct layout: the operands go in
    /// through <see cref="Vector128.CreateScalar(ulong)"/> and the limbs come back out through explicit element reads.
    /// The control byte selects the low half of both operands, which is the only pairing the scalar seam ever wants.
    /// </remarks>
    /// <exception cref="PlatformNotSupportedException">The carryless-multiply instruction set is unavailable. Every caller except a tier verifier reaches this kernel through <see cref="CarrylessMultiply64(ulong, ulong)"/>, which never calls it in that case.</exception>
    internal static (ulong Low, ulong High) CarrylessMultiply64Hardware(ulong left, ulong right) {
        var product = Pclmulqdq.CarrylessMultiply(
            left: Vector128.CreateScalar(value: left),
            right: Vector128.CreateScalar(value: right),
            control: 0x00
        );

        return (Low: product.GetElement(index: 0), High: product.GetElement(index: 1));
    }
    /// <summary>Computes the exact carryless product of two polynomials packed into <see cref="ulong"/> values without any hardware assistance.</summary>
    /// <param name="left">The left operand; bit <c>i</c> is the coefficient of <c>t^i</c>.</param>
    /// <param name="right">The right operand; bit <c>i</c> is the coefficient of <c>t^i</c>.</param>
    /// <returns>The exact 128-bit product, split into its low and high 64-bit limbs.</returns>
    /// <remarks>
    /// The product is assembled from four 32-bit carryless products in the schoolbook arrangement. The kernel is
    /// table-free, branch-free, allocation-free, and constant-time, and it queries no instruction-set support, so a
    /// verifier can run it alongside the hardware tier within a single process.
    /// </remarks>
    internal static (ulong Low, ulong High) CarrylessMultiply64Portable(ulong left, ulong right) {
        var a0 = ((uint)left);
        var a1 = ((uint)(left >>> 32));
        var b0 = ((uint)right);
        var b1 = ((uint)(right >>> 32));
        var middle = (CarrylessMultiply32(left: a0, right: b1) ^ CarrylessMultiply32(left: a1, right: b0));

        return (
            Low: (CarrylessMultiply32(left: a0, right: b0) ^ (middle << 32)),
            High: (CarrylessMultiply32(left: a1, right: b1) ^ (middle >>> 32))
        );
    }
    /// <summary>Computes the exact carryless product of two packed polynomials, split into two limbs of the carrier's own width.</summary>
    /// <typeparam name="T">The packed element carrier.</typeparam>
    /// <param name="left">The left operand; bit <c>i</c> is the coefficient of <c>t^i</c>.</param>
    /// <param name="right">The right operand; bit <c>i</c> is the coefficient of <c>t^i</c>.</param>
    /// <returns>The exact product as a low limb and a high limb, each of the carrier's width.</returns>
    /// <remarks>
    /// The 128-bit carrier uses the schoolbook arrangement of four independent carryless products rather than the three
    /// of a recursive split: the four fill the multiply latency with instruction-level parallelism and keep the
    /// combining XOR chain short, where the recursive split trades a shorter instruction count for a longer chain.
    /// </remarks>
    /// <exception cref="NotSupportedException"><typeparamref name="T"/> is not one of the supported element carriers. A binary field requires a fixed carrier width.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static (T Low, T High) CarrylessMultiplyWide<T>(T left, T right) where T : IBinaryInteger<T>, IUnsignedNumber<T> {
        switch (left) {
            case byte:
            case ushort:
            case uint: {
                var narrow = CarrylessMultiply64(
                    left: ulong.CreateTruncating(value: left),
                    right: ulong.CreateTruncating(value: right)
                ).Low;

                return (
                    Low: T.CreateTruncating(value: narrow),
                    High: T.CreateTruncating(value: (narrow >>> CarrierBitCount<T>()))
                );
            }
            case ulong: {
                var wide = CarrylessMultiply64(
                    left: ulong.CreateTruncating(value: left),
                    right: ulong.CreateTruncating(value: right)
                );

                return (Low: T.CreateTruncating(value: wide.Low), High: T.CreateTruncating(value: wide.High));
            }
            case UInt128: {
                var leftBits = UInt128.CreateTruncating(value: left);
                var rightBits = UInt128.CreateTruncating(value: right);
                var a0 = ((ulong)leftBits);
                var a1 = ((ulong)(leftBits >>> 64));
                var b0 = ((ulong)rightBits);
                var b1 = ((ulong)(rightBits >>> 64));
                var lower = CarrylessMultiply64(left: a0, right: b0);
                var upper = CarrylessMultiply64(left: a1, right: b1);
                var firstCross = CarrylessMultiply64(left: a0, right: b1);
                var secondCross = CarrylessMultiply64(left: a1, right: b0);
                var middleLow = (firstCross.Low ^ secondCross.Low);
                var middleHigh = (firstCross.High ^ secondCross.High);

                return (
                    Low: T.CreateTruncating(value: ((((UInt128)(lower.High ^ middleLow)) << 64) | lower.Low)),
                    High: T.CreateTruncating(value: ((((UInt128)upper.High) << 64) | (upper.Low ^ middleHigh)))
                );
            }
            default:
                break;
        }

        return ThrowUnsupportedCarrier<T>();
    }
    /// <summary>Gets the total number of bits occupied by the element carrier.</summary>
    /// <typeparam name="T">The packed element carrier.</typeparam>
    /// <returns>The carrier's width in bits.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int CarrierBitCount<T>() where T : IBinaryInteger<T>, IUnsignedNumber<T> =>
        (Unsafe.SizeOf<T>() << 3);
    /// <summary>Raises a field element to a caller-supplied power.</summary>
    /// <typeparam name="T">The packed element carrier.</typeparam>
    /// <param name="value">The reduced element to raise.</param>
    /// <param name="exponent">The exponent; zero yields the multiplicative identity for every <paramref name="value"/>, including zero.</param>
    /// <param name="degree">The field's degree.</param>
    /// <param name="tail">The modulus tail.</param>
    /// <returns><paramref name="value"/> raised to <paramref name="exponent"/>, reduced.</returns>
    /// <remarks>Square-and-multiply over the exponent's binary expansion. The operation count depends on the exponent, so this is not constant-time in it.</remarks>
    internal static T Exponentiate<T>(T value, ulong exponent, int degree, T tail) where T : IBinaryInteger<T>, IUnsignedNumber<T> {
        var power = value;
        var result = T.One;

        while (0UL != exponent) {
            if (0UL != (exponent & 1UL)) { result = Multiply(left: result, right: power, degree: degree, tail: tail); }

            exponent >>>= 1;

            if (0UL != exponent) { power = Multiply(left: power, right: power, degree: degree, tail: tail); }
        }

        return result;
    }
    /// <summary>Computes the multiplicative inverse of a non-zero field element.</summary>
    /// <typeparam name="T">The packed element carrier.</typeparam>
    /// <param name="value">The reduced, non-zero element to invert.</param>
    /// <param name="degree">The field's degree.</param>
    /// <param name="tail">The modulus tail.</param>
    /// <returns>The element whose product with <paramref name="value"/> is one.</returns>
    /// <remarks>
    /// The Itoh–Tsujii Frobenius addition chain: <c>value^(2^degree - 2)</c> is assembled from the doubling identity
    /// <c>a^(2^2i - 1) = (a^(2^i - 1))^(2^i) * a^(2^i - 1)</c>, walked over the binary expansion of <c>degree - 1</c>.
    /// The chain's shape therefore depends only on the degree and never on the value, which makes it uniform-time. It
    /// replaces roughly half of a naive Fermat exponentiation's general multiplies with repeated squarings; on the
    /// hardware tier a squaring is itself a carryless multiply, so the saving is a factor near two rather than the
    /// order of magnitude a "squarings are free" framing would suggest.
    /// </remarks>
    /// <exception cref="DivideByZeroException"><paramref name="value"/> is zero.</exception>
    internal static T Inverse<T>(T value, int degree, T tail) where T : IBinaryInteger<T>, IUnsignedNumber<T> {
        if (T.Zero == value) { throw new DivideByZeroException("Zero has no multiplicative inverse."); }
        if (1 == degree) { return T.One; }

        var exponent = (degree - 1);
        var reach = 1;
        var result = value;

        for (var bit = (BitOperations.Log2(value: ((uint)exponent)) - 1); (0 <= bit); --bit) {
            result = Multiply(
                left: FrobeniusRepeat(value: result, count: reach, degree: degree, tail: tail),
                right: result,
                degree: degree,
                tail: tail
            );
            reach <<= 1;

            if (0 != ((exponent >>> bit) & 1)) {
                result = Multiply(
                    left: Multiply(left: result, right: result, degree: degree, tail: tail),
                    right: value,
                    degree: degree,
                    tail: tail
                );
                ++reach;
            }
        }

        // `result` is value^(2^(degree - 1) - 1); one further Frobenius step lands on value^(2^degree - 2).
        return Multiply(left: result, right: result, degree: degree, tail: tail);
    }
    /// <summary>Gets whether the modulus <c>t^degree + tail</c> is irreducible over the two-element field.</summary>
    /// <typeparam name="T">The packed element carrier.</typeparam>
    /// <param name="degree">The field's degree.</param>
    /// <param name="tail">The modulus tail.</param>
    /// <returns><see langword="true"/> when the modulus is irreducible; otherwise <see langword="false"/>.</returns>
    /// <remarks>
    /// The Ben-Or/Rabin criterion, in the stronger form that tests every exponent through half the degree rather than
    /// only the quotients by the degree's prime divisors. It is construction-time validation and never a hot path.
    /// </remarks>
    internal static bool IsIrreducible<T>(int degree, T tail) where T : IBinaryInteger<T>, IUnsignedNumber<T> {
        if (1 > degree) { return false; }

        var indeterminate = ReduceWide(low: (T.One << 1), high: T.Zero, degree: degree, tail: tail);
        var power = indeterminate;

        for (var exponent = 1; (exponent <= degree); ++exponent) {
            power = Multiply(left: power, right: power, degree: degree, tail: tail);

            if ((exponent <= (degree >> 1)) &&
                (T.One != ModulusGreatestCommonDivisor(value: (power ^ indeterminate), degree: degree, tail: tail))) {
                return false;
            }
        }

        return (power == indeterminate);
    }
    /// <summary>Gets whether the instruction set a region-scaling rung is built on is available on the current machine.</summary>
    /// <param name="tier">The rung to test.</param>
    /// <returns><see langword="true"/> when the rung may be executed; otherwise <see langword="false"/>.</returns>
    /// <remarks>
    /// Every width is an independent processor-feature leaf: 256-bit Galois-field affine support implies neither the
    /// 128-bit nor the 512-bit form, so each rung is asked its own question. The 512-bit rungs are deliberately not
    /// gated on vector hardware acceleration, which reports true everywhere because the wide vector types are emulated
    /// in narrower chunks when the hardware is absent — a correct but slow path that would outrank the 256-bit rung.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsRegionTierSupported(BinaryFieldRegionTier tier) =>
        tier switch {
            BinaryFieldRegionTier.Affine512 => Gfni.V512.IsSupported,
            BinaryFieldRegionTier.Split512 => Avx512BW.IsSupported,
            BinaryFieldRegionTier.Affine256 => Gfni.V256.IsSupported,
            BinaryFieldRegionTier.Split256 => Avx2.IsSupported,
            BinaryFieldRegionTier.Affine128 => Gfni.IsSupported,
            BinaryFieldRegionTier.Split128 => Ssse3.IsSupported,
            BinaryFieldRegionTier.Scalar => true,
            _ => false,
        };
    /// <summary>Multiplies two reduced field elements.</summary>
    /// <typeparam name="T">The packed element carrier.</typeparam>
    /// <param name="left">The reduced left operand.</param>
    /// <param name="right">The reduced right operand.</param>
    /// <param name="degree">The field's degree.</param>
    /// <param name="tail">The modulus tail.</param>
    /// <returns>The reduced product.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static T Multiply<T>(T left, T right, int degree, T tail) where T : IBinaryInteger<T>, IUnsignedNumber<T> {
        var product = CarrylessMultiplyWide(left: left, right: right);

        return ReduceWide(low: product.Low, high: product.High, degree: degree, tail: tail);
    }
    /// <summary>Scales a region of packed field elements by a single element, either accumulating into the destination or overwriting it.</summary>
    /// <typeparam name="T">The packed element carrier.</typeparam>
    /// <param name="destination">The region to write, whose length matches <paramref name="source"/>.</param>
    /// <param name="source">The reduced region to scale.</param>
    /// <param name="scalar">The reduced element to scale by.</param>
    /// <param name="accumulate"><see langword="true"/> to add the scaled region into the destination; <see langword="false"/> to overwrite it.</param>
    /// <param name="degree">The field's degree.</param>
    /// <param name="tail">The modulus tail.</param>
    /// <remarks>
    /// The ladder is widest-vector-first, and within a width the hardware Galois-field affine transform precedes the
    /// nibble-split byte shuffle, because region throughput is dominated by vector width while both kernel families
    /// cost one or two vector operations per vector. A rung whose instruction set is absent, and a nibble-split rung
    /// whose table setup a short region would not earn back, both fall through to the next rung and ultimately to the
    /// element-at-a-time loop.
    /// </remarks>
    internal static void MultiplyAccumulateRegion<T>(Span<T> destination, ReadOnlySpan<T> source, T scalar, bool accumulate, int degree, T tail) where T : IBinaryInteger<T>, IUnsignedNumber<T> {
        switch (scalar) {
            case byte:
                if (TryMultiplyAccumulateRegionByte(destination: destination, source: source, scalar: scalar, accumulate: accumulate, degree: degree, tail: tail)) { return; }

                break;
            case ushort:
                if (TryMultiplyAccumulateRegionWide(destination: destination, source: source, scalar: scalar, accumulate: accumulate, degree: degree, tail: tail)) { return; }

                break;
            default:
                break;
        }

        MultiplyAccumulateRegionScalar(destination: destination, source: source, scalar: scalar, accumulate: accumulate, degree: degree, tail: tail);
    }
    /// <summary>Scales a region of byte-wide field elements through the 128-bit Galois-field affine transform.</summary>
    /// <param name="destination">The region to write, whose length matches <paramref name="source"/>.</param>
    /// <param name="source">The reduced region to scale.</param>
    /// <param name="scalar">The reduced element to scale by.</param>
    /// <param name="accumulate"><see langword="true"/> to add the scaled region into the destination; <see langword="false"/> to overwrite it.</param>
    /// <param name="degree">The field's degree, which is at most eight.</param>
    /// <param name="tail">The modulus tail.</param>
    /// <remarks>Elements past the last whole vector are finished by the element-at-a-time loop rather than by a masked store, which would be a further kernel to prove for the last few bytes of a region measured in kilobytes.</remarks>
    /// <exception cref="PlatformNotSupportedException">The 128-bit Galois-field instruction set is unavailable.</exception>
    internal static void MultiplyAccumulateRegionAffine128(Span<byte> destination, ReadOnlySpan<byte> source, byte scalar, bool accumulate, int degree, byte tail) {
        var count = destination.Length;
        var index = 0;
        var matrix = Vector128.Create(value: AffineMatrix(scalar: scalar, degree: degree, tail: tail)).AsByte();
        // A zero mask turns accumulation into a plain store. The destination is still loaded either way, so the rung
        // keeps one branch-free loop body instead of two nearly identical ones.
        var keep = (accumulate ? Vector128<byte>.AllBitsSet : Vector128<byte>.Zero);
        ref var destinationBytes = ref MemoryMarshal.GetReference(span: destination);
        ref var sourceBytes = ref MemoryMarshal.GetReference(span: source);

        for (; ((index + 16) <= count); index += 16) {
            (Gfni.GaloisFieldAffineTransform(
                x: Vector128.LoadUnsafe(source: ref sourceBytes, elementOffset: ((nuint)index)),
                a: matrix,
                b: 0
             ) ^ (Vector128.LoadUnsafe(source: ref destinationBytes, elementOffset: ((nuint)index)) & keep))
                .StoreUnsafe(destination: ref destinationBytes, elementOffset: ((nuint)index));
        }

        MultiplyAccumulateRegionScalar(
            destination: destination[index..],
            source: source[index..],
            scalar: scalar,
            accumulate: accumulate,
            degree: degree,
            tail: tail
        );
    }
    /// <summary>Scales a region of byte-wide field elements through the 256-bit Galois-field affine transform.</summary>
    /// <param name="destination">The region to write, whose length matches <paramref name="source"/>.</param>
    /// <param name="source">The reduced region to scale.</param>
    /// <param name="scalar">The reduced element to scale by.</param>
    /// <param name="accumulate"><see langword="true"/> to add the scaled region into the destination; <see langword="false"/> to overwrite it.</param>
    /// <param name="degree">The field's degree, which is at most eight.</param>
    /// <param name="tail">The modulus tail.</param>
    /// <remarks>The matrix qword is broadcast explicitly because the transform reads its matrix operand once per eight-byte group and does not broadcast it itself.</remarks>
    /// <exception cref="PlatformNotSupportedException">The 256-bit Galois-field instruction set is unavailable.</exception>
    internal static void MultiplyAccumulateRegionAffine256(Span<byte> destination, ReadOnlySpan<byte> source, byte scalar, bool accumulate, int degree, byte tail) {
        var count = destination.Length;
        var index = 0;
        var matrix = Vector256.Create(value: AffineMatrix(scalar: scalar, degree: degree, tail: tail)).AsByte();
        var keep = (accumulate ? Vector256<byte>.AllBitsSet : Vector256<byte>.Zero);
        ref var destinationBytes = ref MemoryMarshal.GetReference(span: destination);
        ref var sourceBytes = ref MemoryMarshal.GetReference(span: source);

        for (; ((index + 32) <= count); index += 32) {
            (Gfni.V256.GaloisFieldAffineTransform(
                x: Vector256.LoadUnsafe(source: ref sourceBytes, elementOffset: ((nuint)index)),
                a: matrix,
                b: 0
             ) ^ (Vector256.LoadUnsafe(source: ref destinationBytes, elementOffset: ((nuint)index)) & keep))
                .StoreUnsafe(destination: ref destinationBytes, elementOffset: ((nuint)index));
        }

        MultiplyAccumulateRegionScalar(
            destination: destination[index..],
            source: source[index..],
            scalar: scalar,
            accumulate: accumulate,
            degree: degree,
            tail: tail
        );
    }
    /// <summary>Scales a region of byte-wide field elements through the 512-bit Galois-field affine transform.</summary>
    /// <param name="destination">The region to write, whose length matches <paramref name="source"/>.</param>
    /// <param name="source">The reduced region to scale.</param>
    /// <param name="scalar">The reduced element to scale by.</param>
    /// <param name="accumulate"><see langword="true"/> to add the scaled region into the destination; <see langword="false"/> to overwrite it.</param>
    /// <param name="degree">The field's degree, which is at most eight.</param>
    /// <param name="tail">The modulus tail.</param>
    /// <exception cref="PlatformNotSupportedException">The 512-bit Galois-field instruction set is unavailable.</exception>
    internal static void MultiplyAccumulateRegionAffine512(Span<byte> destination, ReadOnlySpan<byte> source, byte scalar, bool accumulate, int degree, byte tail) {
        var count = destination.Length;
        var index = 0;
        var matrix = Vector512.Create(value: AffineMatrix(scalar: scalar, degree: degree, tail: tail)).AsByte();
        var keep = (accumulate ? Vector512<byte>.AllBitsSet : Vector512<byte>.Zero);
        ref var destinationBytes = ref MemoryMarshal.GetReference(span: destination);
        ref var sourceBytes = ref MemoryMarshal.GetReference(span: source);

        for (; ((index + 64) <= count); index += 64) {
            (Gfni.V512.GaloisFieldAffineTransform(
                x: Vector512.LoadUnsafe(source: ref sourceBytes, elementOffset: ((nuint)index)),
                a: matrix,
                b: 0
             ) ^ (Vector512.LoadUnsafe(source: ref destinationBytes, elementOffset: ((nuint)index)) & keep))
                .StoreUnsafe(destination: ref destinationBytes, elementOffset: ((nuint)index));
        }

        MultiplyAccumulateRegionScalar(
            destination: destination[index..],
            source: source[index..],
            scalar: scalar,
            accumulate: accumulate,
            degree: degree,
            tail: tail
        );
    }
    /// <summary>Scales a region of packed field elements one element at a time.</summary>
    /// <typeparam name="T">The packed element carrier.</typeparam>
    /// <param name="destination">The region to write, whose length matches <paramref name="source"/>.</param>
    /// <param name="source">The reduced region to scale.</param>
    /// <param name="scalar">The reduced element to scale by.</param>
    /// <param name="accumulate"><see langword="true"/> to add the scaled region into the destination; <see langword="false"/> to overwrite it.</param>
    /// <param name="degree">The field's degree.</param>
    /// <param name="tail">The modulus tail.</param>
    /// <remarks>The reference rung. It queries no instruction-set support, runs at every carrier and degree, and is what every vector rung is compared against.</remarks>
    internal static void MultiplyAccumulateRegionScalar<T>(Span<T> destination, ReadOnlySpan<T> source, T scalar, bool accumulate, int degree, T tail) where T : IBinaryInteger<T>, IUnsignedNumber<T> {
        var count = destination.Length;

        if (accumulate) {
            for (var index = 0; (index < count); ++index) {
                destination[index] ^= Multiply(left: scalar, right: source[index], degree: degree, tail: tail);
            }

            return;
        }

        for (var index = 0; (index < count); ++index) {
            destination[index] = Multiply(left: scalar, right: source[index], degree: degree, tail: tail);
        }
    }
    /// <summary>Scales a region of byte-wide field elements through a 128-bit nibble-split table shuffle.</summary>
    /// <param name="destination">The region to write, whose length matches <paramref name="source"/>.</param>
    /// <param name="source">The reduced region to scale.</param>
    /// <param name="scalar">The reduced element to scale by.</param>
    /// <param name="accumulate"><see langword="true"/> to add the scaled region into the destination; <see langword="false"/> to overwrite it.</param>
    /// <param name="degree">The field's degree, which is at most eight.</param>
    /// <param name="tail">The modulus tail.</param>
    /// <remarks>
    /// Every element is the sum of the scalar times its low nibble and the scalar times its high nibble, so two
    /// sixteen-entry tables built through the field's own multiply cover the whole byte range. The lookups use the
    /// per-lane <see cref="Ssse3.Shuffle(Vector128{byte}, Vector128{byte})"/> rather than the cross-platform shuffle:
    /// the indices are masked to a nibble, so no lane's top bit is set and the two select the identical entry.
    /// </remarks>
    /// <exception cref="PlatformNotSupportedException">The 128-bit byte-shuffle instruction set is unavailable.</exception>
    internal static void MultiplyAccumulateRegionSplit128(Span<byte> destination, ReadOnlySpan<byte> source, byte scalar, bool accumulate, int degree, byte tail) {
        Span<byte> highTable = stackalloc byte[16];
        Span<byte> lowTable = stackalloc byte[16];

        BuildSplitTables(scalar: scalar, degree: degree, tail: tail, lowTable: lowTable, highTable: highTable);

        var count = destination.Length;
        var index = 0;
        var highVector = Vector128.Create(values: ((ReadOnlySpan<byte>)highTable));
        var lowVector = Vector128.Create(values: ((ReadOnlySpan<byte>)lowTable));
        var nibble = Vector128.Create(value: ((byte)0x0FU));
        var keep = (accumulate ? Vector128<byte>.AllBitsSet : Vector128<byte>.Zero);
        ref var destinationBytes = ref MemoryMarshal.GetReference(span: destination);
        ref var sourceBytes = ref MemoryMarshal.GetReference(span: source);

        for (; ((index + 16) <= count); index += 16) {
            var block = Vector128.LoadUnsafe(source: ref sourceBytes, elementOffset: ((nuint)index));
            // No vector instruction set has a byte-wide logical shift, so the high nibble is extracted with a 16-bit
            // shift and the mask below discards the four bits that crossed the byte boundary.
            var high = (Vector128.ShiftRightLogical(vector: block.AsUInt16(), shiftCount: 4).AsByte() & nibble);
            var low = (block & nibble);
            var product = (Ssse3.Shuffle(value: lowVector, mask: low) ^ Ssse3.Shuffle(value: highVector, mask: high));

            (product ^ (Vector128.LoadUnsafe(source: ref destinationBytes, elementOffset: ((nuint)index)) & keep))
                .StoreUnsafe(destination: ref destinationBytes, elementOffset: ((nuint)index));
        }

        MultiplyAccumulateRegionScalar(
            destination: destination[index..],
            source: source[index..],
            scalar: scalar,
            accumulate: accumulate,
            degree: degree,
            tail: tail
        );
    }
    /// <summary>Scales a region of byte-wide field elements through a 256-bit nibble-split table shuffle.</summary>
    /// <param name="destination">The region to write, whose length matches <paramref name="source"/>.</param>
    /// <param name="source">The reduced region to scale.</param>
    /// <param name="scalar">The reduced element to scale by.</param>
    /// <param name="accumulate"><see langword="true"/> to add the scaled region into the destination; <see langword="false"/> to overwrite it.</param>
    /// <param name="degree">The field's degree, which is at most eight.</param>
    /// <param name="tail">The modulus tail.</param>
    /// <remarks>
    /// The sixteen-entry tables are replicated into every 128-bit lane and every index is masked to a nibble, so the
    /// per-lane <see cref="Avx2.Shuffle(Vector256{byte}, Vector256{byte})"/> selects the same entry the cross-platform
    /// shuffle would: no index crosses a lane and no lane's top bit is set.
    /// </remarks>
    /// <exception cref="PlatformNotSupportedException">The 256-bit byte-shuffle instruction set is unavailable.</exception>
    internal static void MultiplyAccumulateRegionSplit256(Span<byte> destination, ReadOnlySpan<byte> source, byte scalar, bool accumulate, int degree, byte tail) {
        Span<byte> highTable = stackalloc byte[16];
        Span<byte> lowTable = stackalloc byte[16];

        BuildSplitTables(scalar: scalar, degree: degree, tail: tail, lowTable: lowTable, highTable: highTable);

        var count = destination.Length;
        var index = 0;
        var highHalf = Vector128.Create(values: ((ReadOnlySpan<byte>)highTable));
        var lowHalf = Vector128.Create(values: ((ReadOnlySpan<byte>)lowTable));
        var highVector = Vector256.Create(lower: highHalf, upper: highHalf);
        var lowVector = Vector256.Create(lower: lowHalf, upper: lowHalf);
        var nibble = Vector256.Create(value: ((byte)0x0FU));
        var keep = (accumulate ? Vector256<byte>.AllBitsSet : Vector256<byte>.Zero);
        ref var destinationBytes = ref MemoryMarshal.GetReference(span: destination);
        ref var sourceBytes = ref MemoryMarshal.GetReference(span: source);

        for (; ((index + 32) <= count); index += 32) {
            var block = Vector256.LoadUnsafe(source: ref sourceBytes, elementOffset: ((nuint)index));
            var high = (Vector256.ShiftRightLogical(vector: block.AsUInt16(), shiftCount: 4).AsByte() & nibble);
            var low = (block & nibble);
            var product = (Avx2.Shuffle(value: lowVector, mask: low) ^ Avx2.Shuffle(value: highVector, mask: high));

            (product ^ (Vector256.LoadUnsafe(source: ref destinationBytes, elementOffset: ((nuint)index)) & keep))
                .StoreUnsafe(destination: ref destinationBytes, elementOffset: ((nuint)index));
        }

        MultiplyAccumulateRegionScalar(
            destination: destination[index..],
            source: source[index..],
            scalar: scalar,
            accumulate: accumulate,
            degree: degree,
            tail: tail
        );
    }
    /// <summary>Scales a region of byte-wide field elements through a 512-bit nibble-split table shuffle.</summary>
    /// <param name="destination">The region to write, whose length matches <paramref name="source"/>.</param>
    /// <param name="source">The reduced region to scale.</param>
    /// <param name="scalar">The reduced element to scale by.</param>
    /// <param name="accumulate"><see langword="true"/> to add the scaled region into the destination; <see langword="false"/> to overwrite it.</param>
    /// <param name="degree">The field's degree, which is at most eight.</param>
    /// <param name="tail">The modulus tail.</param>
    /// <remarks>
    /// The sixteen-entry tables are replicated into every 128-bit lane and every index is masked to a nibble, so the
    /// per-lane <see cref="Avx512BW.Shuffle(Vector512{byte}, Vector512{byte})"/> selects the same entry the
    /// cross-platform shuffle would: no index crosses a lane and no lane's top bit is set.
    /// </remarks>
    /// <exception cref="PlatformNotSupportedException">The 512-bit byte-shuffle instruction set is unavailable.</exception>
    internal static void MultiplyAccumulateRegionSplit512(Span<byte> destination, ReadOnlySpan<byte> source, byte scalar, bool accumulate, int degree, byte tail) {
        Span<byte> highTable = stackalloc byte[16];
        Span<byte> lowTable = stackalloc byte[16];

        BuildSplitTables(scalar: scalar, degree: degree, tail: tail, lowTable: lowTable, highTable: highTable);

        var count = destination.Length;
        var index = 0;
        var highQuarter = Vector128.Create(values: ((ReadOnlySpan<byte>)highTable));
        var lowQuarter = Vector128.Create(values: ((ReadOnlySpan<byte>)lowTable));
        var highHalf = Vector256.Create(lower: highQuarter, upper: highQuarter);
        var lowHalf = Vector256.Create(lower: lowQuarter, upper: lowQuarter);
        var highVector = Vector512.Create(lower: highHalf, upper: highHalf);
        var lowVector = Vector512.Create(lower: lowHalf, upper: lowHalf);
        var nibble = Vector512.Create(value: ((byte)0x0FU));
        var keep = (accumulate ? Vector512<byte>.AllBitsSet : Vector512<byte>.Zero);
        ref var destinationBytes = ref MemoryMarshal.GetReference(span: destination);
        ref var sourceBytes = ref MemoryMarshal.GetReference(span: source);

        for (; ((index + 64) <= count); index += 64) {
            var block = Vector512.LoadUnsafe(source: ref sourceBytes, elementOffset: ((nuint)index));
            var high = (Vector512.ShiftRightLogical(vector: block.AsUInt16(), shiftCount: 4).AsByte() & nibble);
            var low = (block & nibble);
            var product = (Avx512BW.Shuffle(value: lowVector, mask: low) ^ Avx512BW.Shuffle(value: highVector, mask: high));

            (product ^ (Vector512.LoadUnsafe(source: ref destinationBytes, elementOffset: ((nuint)index)) & keep))
                .StoreUnsafe(destination: ref destinationBytes, elementOffset: ((nuint)index));
        }

        MultiplyAccumulateRegionScalar(
            destination: destination[index..],
            source: source[index..],
            scalar: scalar,
            accumulate: accumulate,
            degree: degree,
            tail: tail
        );
    }
    /// <summary>Scales a region of sixteen-bit field elements through the 128-bit Galois-field affine transform.</summary>
    /// <param name="destination">The region to write, whose length matches <paramref name="source"/>.</param>
    /// <param name="source">The reduced region to scale.</param>
    /// <param name="scalar">The reduced element to scale by.</param>
    /// <param name="accumulate"><see langword="true"/> to add the scaled region into the destination; <see langword="false"/> to overwrite it.</param>
    /// <param name="degree">The field's degree, which is at most sixteen.</param>
    /// <param name="tail">The modulus tail.</param>
    /// <remarks>
    /// The transform is defined over bytes, so scaling a sixteen-bit element is split into the four byte-to-byte pieces
    /// of the map: each half of the product is a sum of one matrix applied to the low half of the operand and another
    /// applied to the high half. Rotating every element by eight bits presents the opposite half in each byte lane, so
    /// four transforms and a lane-parity blend cover all four pieces.
    /// </remarks>
    /// <exception cref="PlatformNotSupportedException">The 128-bit Galois-field instruction set is unavailable.</exception>
    internal static void MultiplyAccumulateRegionWideAffine128(Span<ushort> destination, ReadOnlySpan<ushort> source, ushort scalar, bool accumulate, int degree, ushort tail) {
        var count = destination.Length;
        var index = 0;
        var matrices = WideAffineMatrices(scalar: scalar, degree: degree, tail: tail);
        var lowFromHigh = Vector128.Create(value: matrices.LowFromHigh).AsByte();
        var lowFromLow = Vector128.Create(value: matrices.LowFromLow).AsByte();
        var highFromHigh = Vector128.Create(value: matrices.HighFromHigh).AsByte();
        var highFromLow = Vector128.Create(value: matrices.HighFromLow).AsByte();
        // The processor is little-endian on every platform this library targets, so byte lane zero of each element
        // holds its low half and lane one its high half; the mask below selects the low halves.
        var lowLanes = Vector128.Create(value: ((ushort)0x00FFU)).AsByte();
        var keep = (accumulate ? Vector128<byte>.AllBitsSet : Vector128<byte>.Zero);
        ref var destinationElements = ref MemoryMarshal.GetReference(span: destination);
        ref var sourceElements = ref MemoryMarshal.GetReference(span: source);

        for (; ((index + 8) <= count); index += 8) {
            var block = Vector128.LoadUnsafe(source: ref sourceElements, elementOffset: ((nuint)index));
            var swapped = (Vector128.ShiftLeft(vector: block, shiftCount: 8) | Vector128.ShiftRightLogical(vector: block, shiftCount: 8));
            var blockBytes = block.AsByte();
            var swappedBytes = swapped.AsByte();
            var lowHalves = (Gfni.GaloisFieldAffineTransform(x: blockBytes, a: lowFromLow, b: 0) ^
                             Gfni.GaloisFieldAffineTransform(x: swappedBytes, a: lowFromHigh, b: 0));
            var highHalves = (Gfni.GaloisFieldAffineTransform(x: blockBytes, a: highFromHigh, b: 0) ^
                              Gfni.GaloisFieldAffineTransform(x: swappedBytes, a: highFromLow, b: 0));
            var product = ((lowHalves & lowLanes) | Vector128.AndNot(left: highHalves, right: lowLanes));

            (product ^ (Vector128.LoadUnsafe(source: ref destinationElements, elementOffset: ((nuint)index)).AsByte() & keep))
                .AsUInt16()
                .StoreUnsafe(destination: ref destinationElements, elementOffset: ((nuint)index));
        }

        MultiplyAccumulateRegionScalar(
            destination: destination[index..],
            source: source[index..],
            scalar: scalar,
            accumulate: accumulate,
            degree: degree,
            tail: tail
        );
    }
    /// <summary>Scales a region of sixteen-bit field elements through the 256-bit Galois-field affine transform.</summary>
    /// <param name="destination">The region to write, whose length matches <paramref name="source"/>.</param>
    /// <param name="source">The reduced region to scale.</param>
    /// <param name="scalar">The reduced element to scale by.</param>
    /// <param name="accumulate"><see langword="true"/> to add the scaled region into the destination; <see langword="false"/> to overwrite it.</param>
    /// <param name="degree">The field's degree, which is at most sixteen.</param>
    /// <param name="tail">The modulus tail.</param>
    /// <exception cref="PlatformNotSupportedException">The 256-bit Galois-field instruction set is unavailable.</exception>
    internal static void MultiplyAccumulateRegionWideAffine256(Span<ushort> destination, ReadOnlySpan<ushort> source, ushort scalar, bool accumulate, int degree, ushort tail) {
        var count = destination.Length;
        var index = 0;
        var matrices = WideAffineMatrices(scalar: scalar, degree: degree, tail: tail);
        var lowFromHigh = Vector256.Create(value: matrices.LowFromHigh).AsByte();
        var lowFromLow = Vector256.Create(value: matrices.LowFromLow).AsByte();
        var highFromHigh = Vector256.Create(value: matrices.HighFromHigh).AsByte();
        var highFromLow = Vector256.Create(value: matrices.HighFromLow).AsByte();
        var lowLanes = Vector256.Create(value: ((ushort)0x00FFU)).AsByte();
        var keep = (accumulate ? Vector256<byte>.AllBitsSet : Vector256<byte>.Zero);
        ref var destinationElements = ref MemoryMarshal.GetReference(span: destination);
        ref var sourceElements = ref MemoryMarshal.GetReference(span: source);

        for (; ((index + 16) <= count); index += 16) {
            var block = Vector256.LoadUnsafe(source: ref sourceElements, elementOffset: ((nuint)index));
            var swapped = (Vector256.ShiftLeft(vector: block, shiftCount: 8) | Vector256.ShiftRightLogical(vector: block, shiftCount: 8));
            var blockBytes = block.AsByte();
            var swappedBytes = swapped.AsByte();
            var lowHalves = (Gfni.V256.GaloisFieldAffineTransform(x: blockBytes, a: lowFromLow, b: 0) ^
                             Gfni.V256.GaloisFieldAffineTransform(x: swappedBytes, a: lowFromHigh, b: 0));
            var highHalves = (Gfni.V256.GaloisFieldAffineTransform(x: blockBytes, a: highFromHigh, b: 0) ^
                              Gfni.V256.GaloisFieldAffineTransform(x: swappedBytes, a: highFromLow, b: 0));
            var product = ((lowHalves & lowLanes) | Vector256.AndNot(left: highHalves, right: lowLanes));

            (product ^ (Vector256.LoadUnsafe(source: ref destinationElements, elementOffset: ((nuint)index)).AsByte() & keep))
                .AsUInt16()
                .StoreUnsafe(destination: ref destinationElements, elementOffset: ((nuint)index));
        }

        MultiplyAccumulateRegionScalar(
            destination: destination[index..],
            source: source[index..],
            scalar: scalar,
            accumulate: accumulate,
            degree: degree,
            tail: tail
        );
    }
    /// <summary>Scales a region of sixteen-bit field elements through the 512-bit Galois-field affine transform.</summary>
    /// <param name="destination">The region to write, whose length matches <paramref name="source"/>.</param>
    /// <param name="source">The reduced region to scale.</param>
    /// <param name="scalar">The reduced element to scale by.</param>
    /// <param name="accumulate"><see langword="true"/> to add the scaled region into the destination; <see langword="false"/> to overwrite it.</param>
    /// <param name="degree">The field's degree, which is at most sixteen.</param>
    /// <param name="tail">The modulus tail.</param>
    /// <exception cref="PlatformNotSupportedException">The 512-bit Galois-field instruction set is unavailable.</exception>
    internal static void MultiplyAccumulateRegionWideAffine512(Span<ushort> destination, ReadOnlySpan<ushort> source, ushort scalar, bool accumulate, int degree, ushort tail) {
        var count = destination.Length;
        var index = 0;
        var matrices = WideAffineMatrices(scalar: scalar, degree: degree, tail: tail);
        var lowFromHigh = Vector512.Create(value: matrices.LowFromHigh).AsByte();
        var lowFromLow = Vector512.Create(value: matrices.LowFromLow).AsByte();
        var highFromHigh = Vector512.Create(value: matrices.HighFromHigh).AsByte();
        var highFromLow = Vector512.Create(value: matrices.HighFromLow).AsByte();
        var lowLanes = Vector512.Create(value: ((ushort)0x00FFU)).AsByte();
        var keep = (accumulate ? Vector512<byte>.AllBitsSet : Vector512<byte>.Zero);
        ref var destinationElements = ref MemoryMarshal.GetReference(span: destination);
        ref var sourceElements = ref MemoryMarshal.GetReference(span: source);

        for (; ((index + 32) <= count); index += 32) {
            var block = Vector512.LoadUnsafe(source: ref sourceElements, elementOffset: ((nuint)index));
            var swapped = (Vector512.ShiftLeft(vector: block, shiftCount: 8) | Vector512.ShiftRightLogical(vector: block, shiftCount: 8));
            var blockBytes = block.AsByte();
            var swappedBytes = swapped.AsByte();
            var lowHalves = (Gfni.V512.GaloisFieldAffineTransform(x: blockBytes, a: lowFromLow, b: 0) ^
                             Gfni.V512.GaloisFieldAffineTransform(x: swappedBytes, a: lowFromHigh, b: 0));
            var highHalves = (Gfni.V512.GaloisFieldAffineTransform(x: blockBytes, a: highFromHigh, b: 0) ^
                              Gfni.V512.GaloisFieldAffineTransform(x: swappedBytes, a: highFromLow, b: 0));
            var product = ((lowHalves & lowLanes) | Vector512.AndNot(left: highHalves, right: lowLanes));

            (product ^ (Vector512.LoadUnsafe(source: ref destinationElements, elementOffset: ((nuint)index)).AsByte() & keep))
                .AsUInt16()
                .StoreUnsafe(destination: ref destinationElements, elementOffset: ((nuint)index));
        }

        MultiplyAccumulateRegionScalar(
            destination: destination[index..],
            source: source[index..],
            scalar: scalar,
            accumulate: accumulate,
            degree: degree,
            tail: tail
        );
    }
    /// <summary>Reduces a two-limb product modulo <c>t^degree + tail</c>.</summary>
    /// <typeparam name="T">The packed element carrier.</typeparam>
    /// <param name="low">The product's low limb.</param>
    /// <param name="high">The product's high limb.</param>
    /// <param name="degree">The field's degree.</param>
    /// <param name="tail">The modulus tail.</param>
    /// <returns>The unique representative of the product's class whose degree is below <paramref name="degree"/>.</returns>
    /// <remarks>
    /// The iterated tail fold. Because <c>t^degree</c> is congruent to the tail, the part of the value at or above
    /// <c>t^degree</c> can be multiplied by the tail and folded back down; the tail's degree is strictly below the
    /// field's, so the folded part's degree strictly decreases and the loop always halts. Nothing is precomputed, so
    /// there is no constant that could be derived differently on two paths, and the iteration count depends only on
    /// the operand values rather than on which multiplication tier produced them.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static T ReduceWide<T>(T low, T high, int degree, T tail) where T : IBinaryInteger<T>, IUnsignedNumber<T> {
        var width = CarrierBitCount<T>();
        // The mask is built by right-shifting an all-ones value rather than as ((one << degree) - one): at
        // degree == width the latter's shift count is masked back to zero and the mask comes out as one.
        var mask = (T.AllBitsSet >>> (width - degree));
        var split = Split(low: low, high: high, degree: degree, mask: mask, width: width);
        var accumulator = split.Low;
        var remainder = split.High;

        // Written as a loop rather than the two passes the canonical minimum-weight moduli happen to need: a dense
        // caller-supplied modulus needs more, and a fixed unroll would be silently wrong for exactly that case.
        while (T.Zero != remainder) {
            var fold = CarrylessMultiplyWide(left: remainder, right: tail);
            var folded = Split(low: fold.Low, high: fold.High, degree: degree, mask: mask, width: width);

            accumulator ^= folded.Low;
            remainder = folded.High;
        }

        return accumulator;
    }
    /// <summary>Computes the square root of a field element.</summary>
    /// <typeparam name="T">The packed element carrier.</typeparam>
    /// <param name="value">The reduced element to take the root of.</param>
    /// <param name="degree">The field's degree.</param>
    /// <param name="tail">The modulus tail.</param>
    /// <returns>The unique element whose square is <paramref name="value"/>.</returns>
    /// <remarks>Squaring is a bijection in characteristic two, and its inverse is <c>degree - 1</c> further squarings.</remarks>
    internal static T SquareRoot<T>(T value, int degree, T tail) where T : IBinaryInteger<T>, IUnsignedNumber<T> =>
        FrobeniusRepeat(value: value, count: (degree - 1), degree: degree, tail: tail);

    /// <summary>Builds the bit matrix that the Galois-field affine transform applies to scale a byte-wide field element.</summary>
    /// <param name="scalar">The reduced element to scale by.</param>
    /// <param name="degree">The field's degree, which is at most eight.</param>
    /// <param name="tail">The modulus tail.</param>
    /// <returns>The packed matrix qword.</returns>
    /// <remarks>
    /// Multiplication by a fixed element is linear over the two-element field, so the whole operation is an eight-by-eight
    /// bit matrix whose column <c>b</c> is the scalar times <c>t^b</c>. Every column is computed through the field's own
    /// multiply, which is what makes this rung inherit the scalar rung's correctness instead of deriving the modulus a
    /// second time. Only the packing is subtle, and it is spelled out at the assignment below.
    /// </remarks>
    private static ulong AffineMatrix(byte scalar, int degree, byte tail) {
        var matrix = 0UL;

        for (var column = 0; (column < 8); ++column) {
            var entry = Multiply(
                left: scalar,
                right: ReduceWide(low: ((byte)(1 << column)), high: ((byte)0U), degree: degree, tail: tail),
                degree: degree,
                tail: tail
            );

            for (var row = 0; (row < 8); ++row) {
                // The transform reads the row governing output bit `row` from byte 7 - row of the matrix qword, so the
                // rows are packed from the top of the qword downwards. Writing them in the obvious order instead
                // produces a byte-reversed matrix that still runs and still looks plausible.
                if (0 != ((entry >>> row) & 1)) { matrix |= (1UL << (((7 - row) * 8) + column)); }
            }
        }

        return matrix;
    }
    /// <summary>Builds the two nibble tables that a split-table rung shuffles through.</summary>
    /// <param name="scalar">The reduced element to scale by.</param>
    /// <param name="degree">The field's degree, which is at most eight.</param>
    /// <param name="tail">The modulus tail.</param>
    /// <param name="lowTable">Receives the scalar times each value a low nibble can take.</param>
    /// <param name="highTable">Receives the scalar times each value a high nibble can take.</param>
    /// <remarks>Both tables are computed through the field's own multiply, so this rung inherits the scalar rung's correctness rather than deriving the modulus a second time.</remarks>
    private static void BuildSplitTables(byte scalar, int degree, byte tail, Span<byte> lowTable, Span<byte> highTable) {
        for (var index = 0; (index < 16); ++index) {
            highTable[index] = Multiply(
                left: scalar,
                right: ReduceWide(low: ((byte)(index << 4)), high: ((byte)0U), degree: degree, tail: tail),
                degree: degree,
                tail: tail
            );
            lowTable[index] = Multiply(
                left: scalar,
                right: ReduceWide(low: ((byte)index), high: ((byte)0U), degree: degree, tail: tail),
                degree: degree,
                tail: tail
            );
        }
    }
    /// <summary>Computes the exact carryless product of two polynomials packed into <see cref="uint"/> values.</summary>
    /// <param name="left">The left operand; bit <c>i</c> is the coefficient of <c>t^i</c>.</param>
    /// <param name="right">The right operand; bit <c>i</c> is the coefficient of <c>t^i</c>.</param>
    /// <returns>The exact 64-bit product.</returns>
    private static ulong CarrylessMultiply32(uint left, uint right) {
        // Comb spacing is four bits and a 32-bit comb holds at most eight set bits, so a slot accumulates at most eight
        // and can never carry into the next slot. That is what makes the ordinary integer multiplies below carryless
        // ones: with no carry, each slot's low bit is exactly the XOR-parity the carryless product wants. The same
        // construction on full 64-bit operands would let a slot reach sixteen, carry, and silently corrupt the
        // neighbouring parity bit.
        var x0 = ((ulong)(left & 0x11111111U));
        var x1 = ((ulong)(left & 0x22222222U));
        var x2 = ((ulong)(left & 0x44444444U));
        var x3 = ((ulong)(left & 0x88888888U));
        var y0 = ((ulong)(right & 0x11111111U));
        var y1 = ((ulong)(right & 0x22222222U));
        var y2 = ((ulong)(right & 0x44444444U));
        var y3 = ((ulong)(right & 0x88888888U));
        // Group the sixteen cross-products by the residue of (i + j) modulo four, then keep only that residue's slots.
        var z0 = (((x0 * y0) ^ (x1 * y3)) ^ ((x2 * y2) ^ (x3 * y1)));
        var z1 = (((x0 * y1) ^ (x1 * y0)) ^ ((x2 * y3) ^ (x3 * y2)));
        var z2 = (((x0 * y2) ^ (x1 * y1)) ^ ((x2 * y0) ^ (x3 * y3)));
        var z3 = (((x0 * y3) ^ (x1 * y2)) ^ ((x2 * y1) ^ (x3 * y0)));

        return (((z0 & 0x1111111111111111UL) | (z1 & 0x2222222222222222UL)) |
                ((z2 & 0x4444444444444444UL) | (z3 & 0x8888888888888888UL)));
    }
    /// <summary>Gets the largest exponent carrying a non-zero coefficient, or minus one for the zero value.</summary>
    /// <typeparam name="T">The packed element carrier.</typeparam>
    /// <param name="value">The packed polynomial to measure.</param>
    /// <returns>The polynomial's degree, or minus one when it is zero.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int DegreeOf<T>(T value) where T : IBinaryInteger<T>, IUnsignedNumber<T> =>
        ((CarrierBitCount<T>() - 1) - int.CreateChecked(value: T.LeadingZeroCount(value: value)));
    /// <summary>Applies the Frobenius map — squaring — a fixed number of times.</summary>
    /// <typeparam name="T">The packed element carrier.</typeparam>
    /// <param name="value">The reduced element to square repeatedly.</param>
    /// <param name="count">The number of squarings to apply.</param>
    /// <param name="degree">The field's degree.</param>
    /// <param name="tail">The modulus tail.</param>
    /// <returns><paramref name="value"/> raised to <c>2^count</c>.</returns>
    private static T FrobeniusRepeat<T>(T value, int count, int degree, T tail) where T : IBinaryInteger<T>, IUnsignedNumber<T> {
        for (var index = 0; (index < count); ++index) {
            value = Multiply(left: value, right: value, degree: degree, tail: tail);
        }

        return value;
    }
    /// <summary>Computes the greatest common divisor of a packed polynomial and the field's modulus.</summary>
    /// <typeparam name="T">The packed element carrier.</typeparam>
    /// <param name="value">The polynomial to test, whose degree is below the field's.</param>
    /// <param name="degree">The field's degree.</param>
    /// <param name="tail">The modulus tail.</param>
    /// <returns>The monic greatest common divisor, or zero when <paramref name="value"/> is zero and the divisor is therefore the modulus itself.</returns>
    /// <remarks>
    /// The modulus needs one bit more than the carrier holds, so only Euclid's first step can touch it. That step is
    /// taken by shifting a running remainder up to <c>t^degree</c> and adding the tail's own remainder, after which
    /// every value fits the carrier and the ordinary Euclidean loop finishes the job.
    /// </remarks>
    private static T ModulusGreatestCommonDivisor<T>(T value, int degree, T tail) where T : IBinaryInteger<T>, IUnsignedNumber<T> {
        // The divisor would be the modulus itself, whose degree is at least one, so the result is certainly not one.
        // Reporting zero says that without ever materializing the value the carrier cannot hold.
        if (T.Zero == value) { return T.Zero; }

        var valueDegree = DegreeOf(value: value);
        var leadingBit = (T.One << valueDegree);
        var accumulator = (value ^ leadingBit);

        for (var exponent = (valueDegree + 1); (exponent <= degree); ++exponent) {
            accumulator <<= 1;

            if (T.Zero != (accumulator & leadingBit)) { accumulator ^= value; }
        }

        return PolynomialGreatestCommonDivisor(
            value: value,
            other: (accumulator ^ PolynomialRemainder(dividend: tail, divisor: value))
        );
    }
    /// <summary>Computes the greatest common divisor of two packed polynomials.</summary>
    /// <typeparam name="T">The packed element carrier.</typeparam>
    /// <param name="value">The first operand.</param>
    /// <param name="other">The second operand.</param>
    /// <returns>The monic greatest common divisor.</returns>
    private static T PolynomialGreatestCommonDivisor<T>(T value, T other) where T : IBinaryInteger<T>, IUnsignedNumber<T> {
        while (T.Zero != other) {
            (value, other) = (other, PolynomialRemainder(dividend: value, divisor: other));
        }

        return value;
    }
    /// <summary>Computes the remainder of one packed polynomial divided by another.</summary>
    /// <typeparam name="T">The packed element carrier.</typeparam>
    /// <param name="dividend">The polynomial to reduce.</param>
    /// <param name="divisor">The polynomial to reduce by.</param>
    /// <returns>The remainder, whose degree is below the divisor's.</returns>
    /// <exception cref="DivideByZeroException"><paramref name="divisor"/> is zero.</exception>
    private static T PolynomialRemainder<T>(T dividend, T divisor) where T : IBinaryInteger<T>, IUnsignedNumber<T> {
        if (T.Zero == divisor) { throw new DivideByZeroException(); }

        var divisorDegree = DegreeOf(value: divisor);

        while ((T.Zero != dividend) && (DegreeOf(value: dividend) >= divisorDegree)) {
            dividend ^= (divisor << (DegreeOf(value: dividend) - divisorDegree));
        }

        return dividend;
    }
    /// <summary>Splits a two-limb value at the field's degree into the part below it and the part at or above it.</summary>
    /// <typeparam name="T">The packed element carrier.</typeparam>
    /// <param name="low">The value's low limb.</param>
    /// <param name="high">The value's high limb.</param>
    /// <param name="degree">The field's degree.</param>
    /// <param name="mask">The mask of the low <paramref name="degree"/> bits.</param>
    /// <param name="width">The carrier's width in bits.</param>
    /// <returns>The part below <c>t^degree</c>, and the part at or above it shifted down to exponent zero.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (T Low, T High) Split<T>(T low, T high, int degree, T mask, int width) where T : IBinaryInteger<T>, IUnsignedNumber<T> {
        // At degree == width both shift counts below reach the carrier's width, where the shift masks them back to zero
        // and would return the value unshifted. That case needs no shifting at all, so it is separated out rather than
        // folded into the general expression.
        if (width == degree) { return (Low: low, High: high); }

        // The upward shift cannot lose a coefficient: a product of two reduced elements has degree at most
        // (2 * degree) - 2, so its high limb has degree at most degree - 2 and stays inside the carrier.
        return (Low: (low & mask), High: ((high << (width - degree)) | (low >>> degree)));
    }
    /// <summary>Throws for an unsupported element carrier, kept out of line so the inlined multiply body stays lean.</summary>
    /// <typeparam name="T">The unsupported element carrier.</typeparam>
    /// <returns>This method never returns; the declared return type only lets a caller use it in a value position.</returns>
    /// <exception cref="NotSupportedException">Always.</exception>
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (T Low, T High) ThrowUnsupportedCarrier<T>() =>
        throw new NotSupportedException(message: $"{typeof(T)} is not a supported binary-field element carrier. A binary field requires a fixed carrier width.");
    /// <summary>Runs the widest available vector rung over a region of byte-wide field elements.</summary>
    /// <typeparam name="T">The packed element carrier, which is <see cref="byte"/> at run time.</typeparam>
    /// <param name="destination">The region to write, whose length matches <paramref name="source"/>.</param>
    /// <param name="source">The reduced region to scale.</param>
    /// <param name="scalar">The reduced element to scale by.</param>
    /// <param name="accumulate"><see langword="true"/> to add the scaled region into the destination; <see langword="false"/> to overwrite it.</param>
    /// <param name="degree">The field's degree.</param>
    /// <param name="tail">The modulus tail.</param>
    /// <returns><see langword="true"/> when a vector rung ran and the region is complete; otherwise <see langword="false"/>.</returns>
    private static bool TryMultiplyAccumulateRegionByte<T>(Span<T> destination, ReadOnlySpan<T> source, T scalar, bool accumulate, int degree, T tail) where T : IBinaryInteger<T>, IUnsignedNumber<T> {
        var count = destination.Length;
        var narrowScalar = byte.CreateTruncating(value: scalar);
        var narrowTail = byte.CreateTruncating(value: tail);
        var narrowDestination = MemoryMarshal.CreateSpan(reference: ref Unsafe.As<T, byte>(source: ref MemoryMarshal.GetReference(span: destination)), length: count);
        var narrowSource = MemoryMarshal.CreateReadOnlySpan(reference: ref Unsafe.As<T, byte>(source: ref MemoryMarshal.GetReference(span: source)), length: count);

        if (IsRegionTierSupported(tier: BinaryFieldRegionTier.Affine512)) {
            MultiplyAccumulateRegionAffine512(destination: narrowDestination, source: narrowSource, scalar: narrowScalar, accumulate: accumulate, degree: degree, tail: narrowTail);

            return true;
        }

        if (IsRegionTierSupported(tier: BinaryFieldRegionTier.Split512) && ((SplitTableAmortizationVectors * 64) <= count)) {
            MultiplyAccumulateRegionSplit512(destination: narrowDestination, source: narrowSource, scalar: narrowScalar, accumulate: accumulate, degree: degree, tail: narrowTail);

            return true;
        }

        if (IsRegionTierSupported(tier: BinaryFieldRegionTier.Affine256)) {
            MultiplyAccumulateRegionAffine256(destination: narrowDestination, source: narrowSource, scalar: narrowScalar, accumulate: accumulate, degree: degree, tail: narrowTail);

            return true;
        }

        if (IsRegionTierSupported(tier: BinaryFieldRegionTier.Split256) && ((SplitTableAmortizationVectors * 32) <= count)) {
            MultiplyAccumulateRegionSplit256(destination: narrowDestination, source: narrowSource, scalar: narrowScalar, accumulate: accumulate, degree: degree, tail: narrowTail);

            return true;
        }

        if (IsRegionTierSupported(tier: BinaryFieldRegionTier.Affine128)) {
            MultiplyAccumulateRegionAffine128(destination: narrowDestination, source: narrowSource, scalar: narrowScalar, accumulate: accumulate, degree: degree, tail: narrowTail);

            return true;
        }

        if (IsRegionTierSupported(tier: BinaryFieldRegionTier.Split128) && ((SplitTableAmortizationVectors * 16) <= count)) {
            MultiplyAccumulateRegionSplit128(destination: narrowDestination, source: narrowSource, scalar: narrowScalar, accumulate: accumulate, degree: degree, tail: narrowTail);

            return true;
        }

        return false;
    }
    /// <summary>Runs the widest available vector rung over a region of sixteen-bit field elements.</summary>
    /// <typeparam name="T">The packed element carrier, which is <see cref="ushort"/> at run time.</typeparam>
    /// <param name="destination">The region to write, whose length matches <paramref name="source"/>.</param>
    /// <param name="source">The reduced region to scale.</param>
    /// <param name="scalar">The reduced element to scale by.</param>
    /// <param name="accumulate"><see langword="true"/> to add the scaled region into the destination; <see langword="false"/> to overwrite it.</param>
    /// <param name="degree">The field's degree.</param>
    /// <param name="tail">The modulus tail.</param>
    /// <returns><see langword="true"/> when a vector rung ran and the region is complete; otherwise <see langword="false"/>.</returns>
    /// <remarks>There is deliberately no nibble-split rung at this width: a sixteen-bit product would need four tables of sixteen sixteen-bit entries, which costs more setup than the four matrices the affine rungs need and buys nothing the affine rungs do not already give.</remarks>
    private static bool TryMultiplyAccumulateRegionWide<T>(Span<T> destination, ReadOnlySpan<T> source, T scalar, bool accumulate, int degree, T tail) where T : IBinaryInteger<T>, IUnsignedNumber<T> {
        var count = destination.Length;
        var wideScalar = ushort.CreateTruncating(value: scalar);
        var wideTail = ushort.CreateTruncating(value: tail);
        var wideDestination = MemoryMarshal.CreateSpan(reference: ref Unsafe.As<T, ushort>(source: ref MemoryMarshal.GetReference(span: destination)), length: count);
        var wideSource = MemoryMarshal.CreateReadOnlySpan(reference: ref Unsafe.As<T, ushort>(source: ref MemoryMarshal.GetReference(span: source)), length: count);

        if (IsRegionTierSupported(tier: BinaryFieldRegionTier.Affine512)) {
            MultiplyAccumulateRegionWideAffine512(destination: wideDestination, source: wideSource, scalar: wideScalar, accumulate: accumulate, degree: degree, tail: wideTail);

            return true;
        }

        if (IsRegionTierSupported(tier: BinaryFieldRegionTier.Affine256)) {
            MultiplyAccumulateRegionWideAffine256(destination: wideDestination, source: wideSource, scalar: wideScalar, accumulate: accumulate, degree: degree, tail: wideTail);

            return true;
        }

        if (IsRegionTierSupported(tier: BinaryFieldRegionTier.Affine128)) {
            MultiplyAccumulateRegionWideAffine128(destination: wideDestination, source: wideSource, scalar: wideScalar, accumulate: accumulate, degree: degree, tail: wideTail);

            return true;
        }

        return false;
    }
    /// <summary>Builds the four bit matrices that the Galois-field affine transform applies to scale a sixteen-bit field element.</summary>
    /// <param name="scalar">The reduced element to scale by.</param>
    /// <param name="degree">The field's degree, which is at most sixteen.</param>
    /// <param name="tail">The modulus tail.</param>
    /// <returns>The four packed matrix qwords, named for the half of the product each produces and the half of the operand each consumes.</returns>
    /// <remarks>
    /// Scaling is linear over the two-element field, so the sixteen-by-sixteen bit matrix of the whole operation splits
    /// into four eight-by-eight blocks that the byte-wide transform can apply. Every column is computed through the
    /// field's own multiply, so this rung inherits the scalar rung's correctness rather than deriving the modulus a
    /// second time; the row packing is the one described at <see cref="AffineMatrix(byte, int, byte)"/>.
    /// </remarks>
    private static (ulong LowFromLow, ulong LowFromHigh, ulong HighFromLow, ulong HighFromHigh) WideAffineMatrices(ushort scalar, int degree, ushort tail) {
        var highFromHigh = 0UL;
        var highFromLow = 0UL;
        var lowFromHigh = 0UL;
        var lowFromLow = 0UL;

        for (var column = 0; (column < 8); ++column) {
            var fromHigh = Multiply(
                left: scalar,
                right: ReduceWide(low: ((ushort)(1 << (column + 8))), high: ((ushort)0U), degree: degree, tail: tail),
                degree: degree,
                tail: tail
            );
            var fromLow = Multiply(
                left: scalar,
                right: ReduceWide(low: ((ushort)(1 << column)), high: ((ushort)0U), degree: degree, tail: tail),
                degree: degree,
                tail: tail
            );

            for (var row = 0; (row < 8); ++row) {
                var bit = (1UL << (((7 - row) * 8) + column));

                if (0 != ((fromHigh >>> row) & 1)) { lowFromHigh |= bit; }
                if (0 != ((fromHigh >>> (row + 8)) & 1)) { highFromHigh |= bit; }
                if (0 != ((fromLow >>> row) & 1)) { lowFromLow |= bit; }
                if (0 != ((fromLow >>> (row + 8)) & 1)) { highFromLow |= bit; }
            }
        }

        return (LowFromLow: lowFromLow, LowFromHigh: lowFromHigh, HighFromLow: highFromLow, HighFromHigh: highFromHigh);
    }
}
