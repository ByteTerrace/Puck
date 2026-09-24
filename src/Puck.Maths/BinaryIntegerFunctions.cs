using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Puck.Maths;

/// <summary>
/// Provides extension methods that implement bit-level and base-10 digit manipulations over arbitrary binary integer
/// types.
/// </summary>
/// <remarks>
/// The routines are written against <see cref="IBinaryInteger{TSelf}"/>, so a single implementation serves every
/// width from <see cref="byte"/> through <see cref="System.Int128"/>. Bit-twiddling operations favor branchless,
/// width-agnostic formulations (and hardware bit-manipulation instructions where available), so a closed generic
/// compiles down to a compact, value-independent instruction sequence. Every hardware path returns the same bits as
/// the portable formulation it replaces.
/// </remarks>
public static class BinaryIntegerFunctions {
    /// <summary>Reinterprets a Boolean as a <typeparamref name="T"/> without branching, yielding <c>1</c> for <see langword="true"/> and <c>0</c> for <see langword="false"/>.</summary>
    /// <typeparam name="T">The binary integer type to produce.</typeparam>
    /// <param name="value">The Boolean to convert.</param>
    /// <returns><c>1</c> when <paramref name="value"/> is <see langword="true"/>; otherwise <c>0</c>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static T As<T>(this bool value) where T : IBinaryInteger<T> =>
        T.CreateTruncating(value: Unsafe.As<bool, byte>(source: ref value));
    /// <summary>Returns <c>1</c> when <paramref name="value"/> is non-zero and <c>0</c> otherwise, without branching.</summary>
    /// <typeparam name="T">The binary integer type.</typeparam>
    /// <param name="value">The value to test.</param>
    /// <returns><c>1</c> when <paramref name="value"/> differs from zero; otherwise <c>0</c>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static T IsNonZero<T>(this T value) where T : IBinaryInteger<T> =>
        (T.Zero != value).As<T>();
    /// <summary>Cyclically rotates the base-10 digits of <paramref name="value"/> by <paramref name="count"/> places, preserving the sign.</summary>
    /// <typeparam name="T">The binary integer type.</typeparam>
    /// <param name="value">The value whose decimal digits are rotated.</param>
    /// <param name="count">The number of digit positions to rotate; positive rotates toward the most significant digit (left) and negative toward the least significant (right). The count is reduced modulo the digit count.</param>
    /// <returns><paramref name="value"/> with its decimal digits rotated, carrying the original sign.</returns>
    /// <remarks>
    /// <paramref name="count"/> is widened to <see cref="long"/> so that <see cref="RotateDigitsRight{T}(T, int)"/> can
    /// negate <see cref="int.MinValue"/> without it wrapping back to itself; the reduction modulo the digit count then
    /// always narrows back to a representable <typeparamref name="T"/>.
    /// </remarks>
    internal static T RotateDigits<T>(this T value, long count) where T : IBinaryInteger<T> {
        var digitCount = value.DigitCount();

        count %= long.CreateTruncating(value: digitCount);

        var countAsT = T.CreateTruncating(value: count);

        if (0 > count) { countAsT += digitCount; }
        if (T.Zero == countAsT) { return value; }

        // Split and recombine on the SIGNED value: the rotation is linear, so the sign factors straight through the two
        // halves and out of the result — no CopySign, and no unrepresentable T.Abs(T.MinValue) up front.
        var factor = BinaryIntegerConstants<T>.Ten.Exponentiate(exponent: (digitCount - countAsT));
        var endDigits = (value / factor);
        var startDigits = (value - (endDigits * factor));

        return ((startDigits * BinaryIntegerConstants<T>.Ten.Exponentiate(exponent: countAsT)) + endDigits);
    }

    // The SWAR forms are separate out-of-line methods so each is its own JIT root with its own inline budget, and so
    // the law suite can hold them to the same oracle as the hardware path on a host that has BMI2.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static TResult BitwisePairBySwar<TInput, TResult>(TInput value, TInput other) where TInput : IBinaryInteger<TInput> where TResult : IBinaryInteger<TResult> {
        var resultBitCount = (Unsafe.SizeOf<TResult>() << 3);
        var laneBitCount = PairLaneBitCount<TInput, TResult>();
        var laneMask = (TResult.AllBitsSet >>> (resultBitCount - laneBitCount));
        var levelCount = BitOperations.Log2(value: ((uint)laneBitCount));
        var evenBits = ClimbButterfly<TResult, SpreadRung<TResult>>(
            levelCount: levelCount,
            value: TResult.CreateTruncating(value: value) & laneMask
        );
        var oddBits = ClimbButterfly<TResult, SpreadRung<TResult>>(
            levelCount: levelCount,
            value: TResult.CreateTruncating(value: other) & laneMask
        );

        return evenBits | (oddBits << 1);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static TResult BitwiseTripleBySwar<TInput, TResult>(TInput value, TInput second, TInput third) where TInput : IBinaryInteger<TInput> where TResult : IBinaryInteger<TResult> {
        var resultBitCount = (Unsafe.SizeOf<TResult>() << 3);
        var laneBitCount = TripleLaneBitCount<TInput, TResult>();
        var laneMask = (TResult.AllBitsSet >>> (resultBitCount - laneBitCount));
        var levelCount = (32 - BitOperations.LeadingZeroCount(value: ((uint)(laneBitCount - 1))));
        var first = ClimbButterfly<TResult, TriadSpreadRung<TResult>>(
            levelCount: levelCount,
            value: TResult.CreateTruncating(value: value) & laneMask
        );
        var middle = ClimbButterfly<TResult, TriadSpreadRung<TResult>>(
            levelCount: levelCount,
            value: TResult.CreateTruncating(value: second) & laneMask
        );
        var last = ClimbButterfly<TResult, TriadSpreadRung<TResult>>(
            levelCount: levelCount,
            value: TResult.CreateTruncating(value: third) & laneMask
        );

        return first | (middle << 1) | (last << 2);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (TResult, TResult) BitwiseUnpairBySwar<TInput, TResult>(TInput value) where TInput : IBinaryInteger<TInput> where TResult : IBinaryInteger<TResult> {
        var slots = 0.NthFermatMask<TInput>();
        // Only min(result width, half the input) bits of each component can land.
        var levelCount = BitOperations.Log2(value: ((uint)Math.Min(
            val1: (Unsafe.SizeOf<TResult>() << 3),
            val2: (Unsafe.SizeOf<TInput>() << 2)
        )));
        var evenBits = ClimbButterfly<TInput, GatherRung<TInput>>(
            levelCount: levelCount,
            value: value & slots
        );
        var oddBits = ClimbButterfly<TInput, GatherRung<TInput>>(
            levelCount: levelCount,
            value: (value >> 1) & slots
        );

        return (TResult.CreateTruncating(value: evenBits), TResult.CreateTruncating(value: oddBits));
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (TResult, TResult, TResult) BitwiseUntripleBySwar<TInput, TResult>(TInput value) where TInput : IBinaryInteger<TInput> where TResult : IBinaryInteger<TResult> {
        var slots = 3.ReplicationMask<TInput>();
        // The first component owns ceil(width / 3) positions, the most of the three; no more of any can land.
        var gatheredBitCount = Math.Min(
            val1: (Unsafe.SizeOf<TResult>() << 3),
            val2: (((Unsafe.SizeOf<TInput>() << 3) + 2) / 3)
        );
        var levelCount = (32 - BitOperations.LeadingZeroCount(value: ((uint)(gatheredBitCount - 1))));
        var first = ClimbButterfly<TInput, TriadGatherRung<TInput>>(
            levelCount: levelCount,
            value: value & slots
        );
        var middle = ClimbButterfly<TInput, TriadGatherRung<TInput>>(
            levelCount: levelCount,
            value: (value >>> 1) & slots
        );
        var last = ClimbButterfly<TInput, TriadGatherRung<TInput>>(
            levelCount: levelCount,
            value: (value >>> 2) & slots
        );

        return (TResult.CreateTruncating(value: first), TResult.CreateTruncating(value: middle), TResult.CreateTruncating(value: last));
    }
    // Only min(input width, half the result) bits of each operand can land in a pair.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int PairLaneBitCount<TInput, TResult>() => Math.Min(
        val1: (Unsafe.SizeOf<TInput>() << 3),
        val2: (Unsafe.SizeOf<TResult>() << 2)
    );
    // The first operand of a triple owns ceil(width / 3) positions, the most of the three; no more of any can land.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int TripleLaneBitCount<TInput, TResult>() => Math.Min(
        val1: (Unsafe.SizeOf<TInput>() << 3),
        val2: (((Unsafe.SizeOf<TResult>() << 3) + 2) / 3)
    );
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static T ClimbButterfly<T, TRung>(T value, int levelCount) where T : IBinaryInteger<T> where TRung : IButterflyRung<T> {
        // Unrolled by hand: the JIT keeps a counted loop as a loop, which rebuilds every mask at run time. Seven rungs
        // cover every built-in carrier. The public members that climb stay NoInlining: inlined into a small caller,
        // the caller's inline budget runs out part-way up the ladder and the remaining rungs stay calls.
        if (0 < levelCount) { value = TRung.Apply(level: 0, levelCount: levelCount, value: value); }
        if (1 < levelCount) { value = TRung.Apply(level: 1, levelCount: levelCount, value: value); }
        if (2 < levelCount) { value = TRung.Apply(level: 2, levelCount: levelCount, value: value); }
        if (3 < levelCount) { value = TRung.Apply(level: 3, levelCount: levelCount, value: value); }
        if (4 < levelCount) { value = TRung.Apply(level: 4, levelCount: levelCount, value: value); }
        if (5 < levelCount) { value = TRung.Apply(level: 5, levelCount: levelCount, value: value); }
        if (6 < levelCount) { value = TRung.Apply(level: 6, levelCount: levelCount, value: value); }

        // The explicit guard lets the JIT drop the wide-carrier loop outright; a bare counted loop survives as dead code.
        if (7 < levelCount) {
            var level = 7;

            do {
                value = TRung.Apply(
                    level: level,
                    levelCount: levelCount,
                    value: value
                );
            } while (++level < levelCount);
        }

        return value;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static T ParallelBitDepositInHardware<T>(T value, T mask) where T : IBinaryInteger<T> {
        if (Unsafe.SizeOf<T>() <= sizeof(uint)) {
            return T.CreateTruncating(value: Bmi2.ParallelBitDeposit(
                mask: ZeroExtendToUInt32(value: mask),
                value: uint.CreateTruncating(value: value)
            ));
        }

        if (Unsafe.SizeOf<T>() == sizeof(ulong)) {
            return T.CreateTruncating(value: Bmi2.X64.ParallelBitDeposit(
                mask: ulong.CreateTruncating(value: mask),
                value: ulong.CreateTruncating(value: value)
            ));
        }

        // A 128-bit word deposits each half separately: the upper half resumes at the source bit after the last one
        // the lower half's mask consumed.
        var wideMask = UInt128.CreateTruncating(value: mask);
        var wideValue = UInt128.CreateTruncating(value: value);
        var lowerMask = ((ulong)wideMask);
        var lower = Bmi2.X64.ParallelBitDeposit(
            mask: lowerMask,
            value: ((ulong)wideValue)
        );
        var upper = Bmi2.X64.ParallelBitDeposit(
            mask: ((ulong)(wideMask >>> 64)),
            value: ((ulong)(wideValue >>> BitOperations.PopCount(value: lowerMask)))
        );

        return T.CreateTruncating(value: new UInt128(
            lower: lower,
            upper: upper
        ));
    }
    private static T ParallelBitDepositInSoftware<T>(T value, T mask) where T : IBinaryInteger<T> {
        var result = T.Zero;

        // Walks the mask's set bits from the bottom, consuming one source bit per mask bit.
        while (T.Zero != mask) {
            var lowest = mask.LowestSetBit();

            result |= lowest & (T.Zero - (value & T.One));
            mask ^= lowest;
            value >>>= 1;
        }

        return result;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static T ParallelBitExtractInHardware<T>(T value, T mask) where T : IBinaryInteger<T> {
        if (Unsafe.SizeOf<T>() <= sizeof(uint)) {
            return T.CreateTruncating(value: Bmi2.ParallelBitExtract(
                mask: ZeroExtendToUInt32(value: mask),
                value: uint.CreateTruncating(value: value)
            ));
        }

        if (Unsafe.SizeOf<T>() == sizeof(ulong)) {
            return T.CreateTruncating(value: Bmi2.X64.ParallelBitExtract(
                mask: ulong.CreateTruncating(value: mask),
                value: ulong.CreateTruncating(value: value)
            ));
        }

        // A 128-bit word extracts each half separately and packs the upper half's bits directly above the lower's.
        var wideMask = UInt128.CreateTruncating(value: mask);
        var wideValue = UInt128.CreateTruncating(value: value);
        var lowerMask = ((ulong)wideMask);
        var lower = Bmi2.X64.ParallelBitExtract(
            mask: lowerMask,
            value: ((ulong)wideValue)
        );
        var upper = Bmi2.X64.ParallelBitExtract(
            mask: ((ulong)(wideMask >>> 64)),
            value: ((ulong)(wideValue >>> 64))
        );

        return T.CreateTruncating(value: (((UInt128)upper) << BitOperations.PopCount(value: lowerMask)) | lower);
    }
    private static T ParallelBitExtractInSoftware<T>(T value, T mask) where T : IBinaryInteger<T> {
        var result = T.Zero;
        var destination = T.One;

        // Walks the mask's set bits from the bottom, packing each selected source bit into the next result bit.
        while (T.Zero != mask) {
            var lowest = mask.LowestSetBit();

            result |= destination & (T.Zero - (value & lowest).IsNonZero());
            mask ^= lowest;
            destination <<= 1;
        }

        return result;
    }
    /// <summary>Returns whether BMI2's deposit and extract instructions cover every bit of <typeparamref name="T"/>, splitting a 128-bit word into two 64-bit halves, and run fast on this host (<see cref="BitManipulation.HasFastParallelBits"/>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HasHardwareBitScatter<T>() where T : IBinaryInteger<T> =>
        (((Unsafe.SizeOf<T>() <= sizeof(uint))
            ? Bmi2.IsSupported
            : ((Unsafe.SizeOf<T>() <= 16) && Bmi2.X64.IsSupported)) && BitManipulation.HasFastParallelBits);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static T LowMaskInRange<T>(int count) where T : IBinaryInteger<T> {
        if (Bmi2.IsSupported && (Unsafe.SizeOf<T>() <= sizeof(uint))) {
            return T.CreateTruncating(value: Bmi2.ZeroHighBits(
                index: ((uint)count),
                value: uint.MaxValue
            ));
        }

        if (Bmi2.X64.IsSupported && (Unsafe.SizeOf<T>() == sizeof(ulong))) {
            return T.CreateTruncating(value: Bmi2.X64.ZeroHighBits(
                index: ((ulong)count),
                value: ulong.MaxValue
            ));
        }

        // Two half shifts: neither reaches the carrier width, so a whole-word count clears every bit instead of the
        // shift count wrapping to zero.
        var half = (count >> 1);

        return ~((T.AllBitsSet << half) << (count - half));
    }
    private static BigInteger NextSamePopulationCount(BigInteger value) {
        // Gosper's hack with unbounded headroom: strictly ascending and never terminal, because a positive
        // BigInteger always has another zero above its highest set bit.
        var x = value.FillFromLowestSetBit();
        var y = int.CreateChecked(value: (BigInteger.TrailingZeroCount(value: value) + BigInteger.One));
        // The increment's carry lands exactly on x's lowest clear bit.
        var carry = (x + BigInteger.One);
        var z = (((carry & ~x) - BigInteger.One) >> y);

        return carry | z;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static BigInteger PermuteBigIntegerBits(BigInteger value) {
        // The two constant strings — all zeros and all ones — are the sole members of their minority-bit classes.
        if (
            value.IsZero ||
            (BigInteger.MinusOne == value)
        ) { return value; }

        // Complementation reverses order and exchanges the two families, so the descending negative walk is the
        // complemented ascending walk of the complement.
        return ((value.Sign > 0)
            ? NextSamePopulationCount(value: value)
            : ~NextSamePopulationCount(value: ~value)
        );
    }
    // The source pattern fits in a ulong and the block divides 64, so every copy sits identically in each half. A whole
    // 128-bit block is an identity. Shared by the wide public path and Fermat masks, whose pattern is known to fit.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static T RepeatWordInto128<T>(ulong value, int blockWidth) where T : IBinaryInteger<T> {
        if (blockWidth == 128) { return T.CreateTruncating(value: value); }

        var half = value.RepeatBits(blockWidth: blockWidth);

        return T.CreateTruncating(value: new UInt128(
            lower: half,
            upper: half
        ));
    }
    // Keep exception construction out of the arithmetic inline budget. Successful calls eliminate these branches
    // when the width and pattern are known; invalid calls retain the same exception type, parameter and message.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowAlignment() =>
        throw new ArgumentOutOfRangeException(
            message: "The alignment must be a positive power of two.",
            paramName: "alignment"
        );
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowExponent() =>
        throw new ArgumentOutOfRangeException(
            message: "The exponent lies outside the range the carrier's width admits.",
            paramName: "exponent"
        );
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowLowMaskCount() =>
        throw new ArgumentOutOfRangeException(
            message: "The count must lie between zero and the carrier's bit width.",
            paramName: "count"
        );
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowReplicationBlockWidth() =>
        throw new ArgumentOutOfRangeException(
            message: "The block width must be positive and no wider than the word.",
            paramName: "blockWidth"
        );
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowReplicationPattern() =>
        throw new ArgumentOutOfRangeException(
            message: "The pattern must fit entirely within one block.",
            paramName: "value"
        );
    /// <summary>Builds the word whose set bits form runs of <paramref name="width"/> ones repeating every <c>3 * width</c> bits, the top run truncated at the word's edge.</summary>
    /// <remarks>These are the one-in-three Morton masks: <c>(2^width - 1) * ReplicationMask(3 * width)</c>, written as <c>(marks &lt;&lt; width) - marks</c> so a 128-bit carrier never multiplies. A period wider than the word holds the first run alone.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static T TriadMask<T>(int width) where T : IBinaryInteger<T> {
        var bitWidth = (Unsafe.SizeOf<T>() << 3);

        if ((3 * width) > bitWidth) {
            return (T.AllBitsSet >>> (bitWidth - Math.Min(
                val1: width,
                val2: bitWidth
            )));
        }

        var marks = (3 * width).ReplicationMask<T>();

        return ((marks << width) - marks);
    }
    /// <summary>Validates the shared block contract and returns the carrier's fixed bit width.</summary>
    /// <typeparam name="T">The binary integer carrier.</typeparam>
    /// <param name="blockWidth">The proposed block width.</param>
    /// <returns>The fixed carrier width in bits.</returns>
    /// <exception cref="NotSupportedException"><typeparamref name="T"/> is <see cref="BigInteger"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="blockWidth"/> is not positive or exceeds the carrier width.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ValidateReplicationBlockWidth<T>(int blockWidth) where T : IBinaryInteger<T> {
        BinaryIntegerConstants<T>.ThrowIfUnbounded(operationName: nameof(ReplicationMask));

        var bitWidth = (Unsafe.SizeOf<T>() << 3);

        if (((uint)(blockWidth - 1)) >= ((uint)bitWidth)) { ThrowReplicationBlockWidth(); }

        return bitWidth;
    }
    // Sub-word carriers reach the 32-bit instructions zero-extended, so a signed mask's sign bits never select
    // positions above the carrier.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ZeroExtendToUInt32<T>(T value) where T : IBinaryInteger<T> =>
        uint.CreateTruncating(value: value) & (uint.MaxValue >>> (32 - (Unsafe.SizeOf<T>() << 3)));

    /// <summary>Rounds <paramref name="value"/> down to a multiple of <paramref name="alignment"/>.</summary>
    /// <typeparam name="T">The binary integer type.</typeparam>
    /// <param name="value">The value to round.</param>
    /// <param name="alignment">The alignment; a positive power of two.</param>
    /// <returns>The largest multiple of <paramref name="alignment"/> that does not exceed <paramref name="value"/>. A negative signed value rounds toward negative infinity.</returns>
    /// <remarks>Clears the bits below the alignment, <c>value &amp; ~(alignment - 1)</c>; nothing can overflow.</remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="alignment"/> is not a positive power of two.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T AlignDown<T>(this T value, T alignment) where T : IBinaryInteger<T> {
        if (!T.IsPow2(value: alignment)) { ThrowAlignment(); }

        return value & ~(alignment - T.One);
    }
    /// <summary>Rounds <paramref name="value"/> up to a multiple of <paramref name="alignment"/>.</summary>
    /// <typeparam name="T">The binary integer type.</typeparam>
    /// <param name="value">The value to round.</param>
    /// <param name="alignment">The alignment; a positive power of two.</param>
    /// <returns>The smallest multiple of <paramref name="alignment"/> that is not below <paramref name="value"/>, wrapped to the carrier: when that multiple lies beyond the top of a fixed-width <typeparamref name="T"/>, the result is its residue modulo the carrier (zero for an unsigned carrier, the signed minimum for a signed one).</returns>
    /// <remarks>Computes <c>(value + (alignment - 1)) &amp; ~(alignment - 1)</c>. Because the carrier's modulus is itself a multiple of every legal alignment, the wrapped sum still rounds to the exact residue.</remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="alignment"/> is not a positive power of two.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T AlignUp<T>(this T value, T alignment) where T : IBinaryInteger<T> {
        if (!T.IsPow2(value: alignment)) { ThrowAlignment(); }

        var slack = (alignment - T.One);

        return (value + slack) & ~slack;
    }
    /// <summary>Returns the bit length of <paramref name="value"/>: the one-based position of its highest set bit.</summary>
    /// <typeparam name="T">The binary integer type.</typeparam>
    /// <param name="value">The value to examine.</param>
    /// <returns>The position of the most significant set bit, counting from <c>1</c>, or <c>0</c> when <paramref name="value"/> is zero. A negative fixed-width value sets its sign bit and so has the carrier's full width.</returns>
    /// <remarks>
    /// "Bit length" has a well-defined meaning even for an unbounded <typeparamref name="T"/> such as <see cref="BigInteger"/>,
    /// so this is computed from the value itself (<see cref="IBinaryInteger{TSelf}.GetShortestBitLength"/>) rather than
    /// from a fixed carrier width in that case. Every fixed-width instantiation keeps the branchless width-minus-leading-
    /// zeros fast path — see <see cref="BinaryIntegerConstants{T}.IsUnbounded"/> for why the guard costs nothing there.
    /// </remarks>
    public static T BitLength<T>(this T value) where T : IBinaryInteger<T> {
        if (typeof(T) == typeof(BigInteger)) {
            return T.CreateTruncating(value: value.GetShortestBitLength());
        }

        return (BinaryIntegerConstants<T>.Size - T.LeadingZeroCount(value: value));
    }
    /// <summary>
    /// Interleaves the bits of two integers into a single value (a Morton, or Z-order, code), placing the bits of
    /// <paramref name="value"/> in the even-indexed positions and the bits of <paramref name="other"/> in the
    /// odd-indexed positions.
    /// </summary>
    /// <typeparam name="TInput">The binary integer type of the operands.</typeparam>
    /// <typeparam name="TResult">The binary integer type of the interleaved result; it must be wide enough to hold the combined bits of both operands.</typeparam>
    /// <param name="value">The operand whose bits occupy the even-indexed positions of the result.</param>
    /// <param name="other">The operand whose bits occupy the odd-indexed positions of the result.</param>
    /// <returns>The Morton code that interleaves the bits of <paramref name="value"/> and <paramref name="other"/>. Operand bits whose position would fall outside <typeparamref name="TResult"/> are dropped, and an operand narrower than half the result leaves the upper positions clear.</returns>
    /// <remarks>
    /// Deposits each operand through <see cref="ParallelBitDeposit{T}(T, T)"/> under the alternating mask when fast BMI2
    /// (<see cref="BitManipulation.HasFastParallelBits"/>) covers <typeparamref name="TResult"/>; otherwise a width-agnostic SWAR ladder spreads each operand under
    /// <see cref="NthFermatMask{T}(int)"/> masks. <see cref="BitwiseUnpair{TInput, TResult}(TInput)"/> is the inverse
    /// operation.
    /// </remarks>
    /// <exception cref="NotSupportedException"><typeparamref name="TInput"/> or <typeparamref name="TResult"/> is <see cref="BigInteger"/>. Interleaving requires a fixed carrier width for both operand and result.</exception>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static TResult BitwisePair<TInput, TResult>(this TInput value, TInput other) where TInput : IBinaryInteger<TInput> where TResult : IBinaryInteger<TResult> {
        BinaryIntegerConstants<TInput>.ThrowIfUnbounded(operationName: nameof(BitwisePair));
        BinaryIntegerConstants<TResult>.ThrowIfUnbounded(operationName: nameof(BitwisePair));

        if (!HasHardwareBitScatter<TResult>()) {
            return BitwisePairBySwar<TInput, TResult>(
                other: other,
                value: value
            );
        }

        var resultBitCount = (Unsafe.SizeOf<TResult>() << 3);
        var laneBitCount = PairLaneBitCount<TInput, TResult>();
        var evenBits = TResult.CreateTruncating(value: value);
        var oddBits = TResult.CreateTruncating(value: other);

        // A deposit reads only as many source bits as its mask has slots, so a lane that fills half the result needs
        // no trimming; a narrower lane must shed the sign extension that CreateTruncating brought in.
        if (laneBitCount < (resultBitCount >> 1)) {
            var laneMask = (TResult.AllBitsSet >>> (resultBitCount - laneBitCount));

            evenBits &= laneMask;
            oddBits &= laneMask;
        }

        var slots = 0.NthFermatMask<TResult>();

        return ParallelBitDepositInHardware(
            mask: slots,
            value: evenBits
        ) | ParallelBitDepositInHardware(
            mask: (slots << 1),
            value: oddBits
        );
    }
    /// <summary>
    /// Interleaves the bits of three integers into a single value (a three-dimensional Morton code), placing bit
    /// <c>i</c> of <paramref name="value"/>, <paramref name="second"/> and <paramref name="third"/> at positions
    /// <c>3i</c>, <c>3i + 1</c> and <c>3i + 2</c>.
    /// </summary>
    /// <typeparam name="TInput">The binary integer type of the operands.</typeparam>
    /// <typeparam name="TResult">The binary integer type of the interleaved result.</typeparam>
    /// <param name="value">The operand whose bits occupy the positions congruent to zero modulo three.</param>
    /// <param name="second">The operand whose bits occupy the positions congruent to one modulo three.</param>
    /// <param name="third">The operand whose bits occupy the positions congruent to two modulo three.</param>
    /// <returns>The Morton code of the three operands. Operand bits whose position would fall outside <typeparamref name="TResult"/> are dropped: a 64-bit result carries 22 bits of <paramref name="value"/> and 21 of each other operand.</returns>
    /// <remarks>
    /// Deposits each operand through <see cref="ParallelBitDeposit{T}(T, T)"/> under the one-in-three
    /// <see cref="ReplicationMask{T}(int)"/> when fast BMI2 (<see cref="BitManipulation.HasFastParallelBits"/>) covers
    /// <typeparamref name="TResult"/>; otherwise a SWAR ladder
    /// spreads each operand under masks of <c>s</c> ones every <c>3s</c> bits, the replication pattern with its last
    /// copy truncated at the word's edge. <see cref="BitwiseUntriple{TInput, TResult}(TInput)"/> is the inverse.
    /// </remarks>
    /// <exception cref="NotSupportedException"><typeparamref name="TInput"/> or <typeparamref name="TResult"/> is <see cref="BigInteger"/>. Interleaving requires a fixed carrier width for both operand and result.</exception>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static TResult BitwiseTriple<TInput, TResult>(this TInput value, TInput second, TInput third) where TInput : IBinaryInteger<TInput> where TResult : IBinaryInteger<TResult> {
        BinaryIntegerConstants<TInput>.ThrowIfUnbounded(operationName: nameof(BitwiseTriple));
        BinaryIntegerConstants<TResult>.ThrowIfUnbounded(operationName: nameof(BitwiseTriple));

        if (!HasHardwareBitScatter<TResult>()) {
            return BitwiseTripleBySwar<TInput, TResult>(
                second: second,
                third: third,
                value: value
            );
        }

        var resultBitCount = (Unsafe.SizeOf<TResult>() << 3);
        var laneBitCount = TripleLaneBitCount<TInput, TResult>();
        var first = TResult.CreateTruncating(value: value);
        var middle = TResult.CreateTruncating(value: second);
        var last = TResult.CreateTruncating(value: third);

        // As for a pair: only a lane narrower than the first operand's slot count needs its sign extension shed.
        if (laneBitCount < ((resultBitCount + 2) / 3)) {
            var laneMask = (TResult.AllBitsSet >>> (resultBitCount - laneBitCount));

            first &= laneMask;
            middle &= laneMask;
            last &= laneMask;
        }

        var slots = 3.ReplicationMask<TResult>();

        return ParallelBitDepositInHardware(
            mask: slots,
            value: first
        ) | ParallelBitDepositInHardware(
            mask: (slots << 1),
            value: middle
        ) | ParallelBitDepositInHardware(
            mask: (slots << 2),
            value: last
        );
    }
    /// <summary>
    /// Separates the interleaved bits of a Morton (Z-order) code into its two components, returning the even-indexed
    /// bits and the odd-indexed bits as a pair.
    /// </summary>
    /// <typeparam name="TInput">The binary integer type of the interleaved input.</typeparam>
    /// <typeparam name="TResult">The binary integer type of each extracted component.</typeparam>
    /// <param name="value">The Morton code to de-interleave.</param>
    /// <returns>A pair whose first element is gathered from the even-indexed bits of <paramref name="value"/> and whose second element is gathered from the odd-indexed bits. A component wider than <typeparamref name="TResult"/> is truncated to it.</returns>
    /// <remarks>
    /// This is the inverse of <see cref="BitwisePair{TInput, TResult}(TInput, TInput)"/>. Extracts through
    /// <see cref="ParallelBitExtract{T}(T, T)"/> when fast BMI2 (<see cref="BitManipulation.HasFastParallelBits"/>) covers
    /// <typeparamref name="TInput"/>; otherwise a width-agnostic SWAR
    /// ladder gathers each component.
    /// </remarks>
    /// <exception cref="NotSupportedException"><typeparamref name="TInput"/> or <typeparamref name="TResult"/> is <see cref="BigInteger"/>. De-interleaving requires a fixed carrier width for both operand and result.</exception>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static (TResult, TResult) BitwiseUnpair<TInput, TResult>(this TInput value) where TInput : IBinaryInteger<TInput> where TResult : IBinaryInteger<TResult> {
        BinaryIntegerConstants<TInput>.ThrowIfUnbounded(operationName: nameof(BitwiseUnpair));
        BinaryIntegerConstants<TResult>.ThrowIfUnbounded(operationName: nameof(BitwiseUnpair));

        if (!HasHardwareBitScatter<TInput>()) { return BitwiseUnpairBySwar<TInput, TResult>(value: value); }

        var slots = 0.NthFermatMask<TInput>();

        return (
            TResult.CreateTruncating(value: ParallelBitExtractInHardware(
                mask: slots,
                value: value
            )),
            TResult.CreateTruncating(value: ParallelBitExtractInHardware(
                mask: (slots << 1),
                value: value
            ))
        );
    }
    /// <summary>
    /// Separates the interleaved bits of a three-dimensional Morton code into its three components, gathering the bits
    /// at positions congruent to zero, one and two modulo three.
    /// </summary>
    /// <typeparam name="TInput">The binary integer type of the interleaved input.</typeparam>
    /// <typeparam name="TResult">The binary integer type of each extracted component.</typeparam>
    /// <param name="value">The Morton code to de-interleave.</param>
    /// <returns>The three components in position order. A component wider than <typeparamref name="TResult"/> is truncated to it.</returns>
    /// <remarks>
    /// This is the inverse of <see cref="BitwiseTriple{TInput, TResult}(TInput, TInput, TInput)"/>. Extracts through
    /// <see cref="ParallelBitExtract{T}(T, T)"/> when fast BMI2 (<see cref="BitManipulation.HasFastParallelBits"/>) covers
    /// <typeparamref name="TInput"/>; otherwise a SWAR ladder
    /// gathers each component.
    /// </remarks>
    /// <exception cref="NotSupportedException"><typeparamref name="TInput"/> or <typeparamref name="TResult"/> is <see cref="BigInteger"/>. De-interleaving requires a fixed carrier width for both operand and result.</exception>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static (TResult, TResult, TResult) BitwiseUntriple<TInput, TResult>(this TInput value) where TInput : IBinaryInteger<TInput> where TResult : IBinaryInteger<TResult> {
        BinaryIntegerConstants<TInput>.ThrowIfUnbounded(operationName: nameof(BitwiseUntriple));
        BinaryIntegerConstants<TResult>.ThrowIfUnbounded(operationName: nameof(BitwiseUntriple));

        if (!HasHardwareBitScatter<TInput>()) { return BitwiseUntripleBySwar<TInput, TResult>(value: value); }

        var slots = 3.ReplicationMask<TInput>();

        return (
            TResult.CreateTruncating(value: ParallelBitExtractInHardware(
                mask: slots,
                value: value
            )),
            TResult.CreateTruncating(value: ParallelBitExtractInHardware(
                mask: (slots << 1),
                value: value
            )),
            TResult.CreateTruncating(value: ParallelBitExtractInHardware(
                mask: (slots << 2),
                value: value
            ))
        );
    }
    /// <summary>Scatters the low bits of <paramref name="value"/> into the set positions of <paramref name="mask"/>, the operation x86 names <c>PDEP</c>.</summary>
    /// <typeparam name="T">The fixed-width binary integer type, signed or unsigned.</typeparam>
    /// <param name="value">The source whose bits are consumed from bit zero upward, one per set bit of <paramref name="mask"/>.</param>
    /// <param name="mask">The destination positions; any bit pattern, including a negative signed value.</param>
    /// <returns>A value whose bit at the <c>k</c>-th lowest set position of <paramref name="mask"/> is bit <c>k</c> of <paramref name="value"/>, with every position outside <paramref name="mask"/> clear.</returns>
    /// <remarks>
    /// One <c>PDEP</c> serves a carrier up to 64 bits wide and two serve a 128-bit carrier when BMI2 is available and fast
    /// (<see cref="BitManipulation.HasFastParallelBits"/>); otherwise a loop visits the mask's set bits from the bottom. Both return the same bits.
    /// <see cref="ParallelBitExtract{T}(T, T)"/> is the inverse on the positions <paramref name="mask"/> selects.
    /// </remarks>
    /// <exception cref="NotSupportedException"><typeparamref name="T"/> is <see cref="BigInteger"/>, which has no fixed word width.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T ParallelBitDeposit<T>(this T value, T mask) where T : IBinaryInteger<T> {
        BinaryIntegerConstants<T>.ThrowIfUnbounded(operationName: nameof(ParallelBitDeposit));

        return (HasHardwareBitScatter<T>()
            ? ParallelBitDepositInHardware(
                mask: mask,
                value: value
            )
            : ParallelBitDepositInSoftware(
                mask: mask,
                value: value
            ));
    }
    /// <summary>Divides <paramref name="value"/> by <paramref name="divisor"/> with a quotient rounded toward positive infinity.</summary>
    /// <typeparam name="T">The binary integer type.</typeparam>
    /// <param name="value">The dividend.</param>
    /// <param name="divisor">The divisor.</param>
    /// <returns>The smallest value that is no less than the exact quotient. This differs from the built-in <c>/</c> — which truncates toward zero — only when the division is inexact and the operands carry matching signs.</returns>
    /// <remarks>The correction is branchless: the truncated quotient is nudged by a single conditional increment, never the <c>-((-value) / divisor)</c> reflection through <see cref="FloorDivide{T}(T, T)"/> — which both spends an extra negation and is unrepresentable when <paramref name="value"/> is the signed minimum. An unsigned <typeparamref name="T"/> increments on every inexact division, since no quotient there discards a negative fraction.</remarks>
    /// <exception cref="DivideByZeroException"><paramref name="divisor"/> is zero.</exception>
    /// <exception cref="OverflowException"><paramref name="value"/> is the signed minimum and <paramref name="divisor"/> is <c>-1</c>; the underlying division rejects that one pair because its quotient is unrepresentable.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T CeilingDivide<T>(this T value, T divisor) where T : IBinaryInteger<T> {
        var (quotient, remainder) = T.DivRem(
            left: value,
            right: divisor
        );

        // The exact quotient sits above the truncated one exactly when the discarded fraction was positive — a non-zero
        // remainder signed the SAME way as the divisor. XOR reads both sign bits in one operation.
        var carry = remainder.IsNonZero() & (T.IsNegative(value: remainder ^ divisor) == false).As<T>();

        return (quotient + carry);
    }
    /// <summary>Returns <paramref name="value"/> with its lowest set bit cleared.</summary>
    /// <typeparam name="T">The binary integer type.</typeparam>
    /// <param name="value">The value to operate on.</param>
    /// <returns><paramref name="value"/> with its least significant set bit turned off, or zero when <paramref name="value"/> is zero.</returns>
    public static T ClearLowestSetBit<T>(this T value) where T : IBinaryInteger<T> =>
        value & (value - T.One);
    /// <summary>Computes the digital root of <paramref name="value"/> — the single base-10 digit reached by repeatedly summing its decimal digits.</summary>
    /// <typeparam name="T">The binary integer type.</typeparam>
    /// <param name="value">The value whose digital root is computed; its sign is ignored.</param>
    /// <returns>Zero when <paramref name="value"/> is zero; otherwise a digit from <c>1</c> through <c>9</c>.</returns>
    /// <remarks>The result is obtained in constant time through the modulo-nine congruence rather than by iterating over the digits.</remarks>
    public static T DigitalRoot<T>(this T value) where T : IBinaryInteger<T> {
        if (T.Zero == value) { return T.Zero; }

        // The magnitude's residue modulo nine equals the magnitude of the SIGNED remainder, so this never materializes
        // |value| — which is unrepresentable at T.MinValue. A zero residue means the magnitude is a non-zero multiple
        // of nine, whose digital root is nine.
        var residue = T.Abs(value: (value % BinaryIntegerConstants<T>.Nine));

        return ((T.Zero == residue)
            ? BinaryIntegerConstants<T>.Nine
            : residue
        );
    }
    /// <summary>Enumerates the base-10 digits of <paramref name="value"/>, from least significant to most significant.</summary>
    /// <typeparam name="T">The binary integer type.</typeparam>
    /// <param name="value">The value whose decimal digits are enumerated; its sign is ignored.</param>
    /// <returns>A lazily evaluated sequence of the decimal digits of <paramref name="value"/>, in least-significant-first order; a single zero is yielded when <paramref name="value"/> is zero.</returns>
    public static IEnumerable<T> EnumerateDigits<T>(this T value) where T : IBinaryInteger<T> {
        // Divide the SIGNED value down and take each remainder's magnitude, so T.MinValue enumerates cleanly rather than
        // throwing on an unrepresentable T.Abs(value). A single-digit remainder's magnitude is always representable.
        var quotient = value;

        do {
            (quotient, var remainder) = T.DivRem(
                left: quotient,
                right: BinaryIntegerConstants<T>.Ten
            );

            yield return T.Abs(value: remainder);
        } while (T.Zero != quotient);
    }
    /// <summary>Raises <paramref name="value"/> to the power <paramref name="exponent"/> using exponentiation by squaring.</summary>
    /// <typeparam name="T">The binary integer type.</typeparam>
    /// <param name="value">The base.</param>
    /// <param name="exponent">The exponent; must be non-negative, since a negative integer power is not representable.</param>
    /// <returns><paramref name="value"/> raised to the power <paramref name="exponent"/>. The result wraps on overflow rather than throwing.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="exponent"/> is negative.</exception>
    public static T Exponentiate<T>(this T value, T exponent) where T : IBinaryInteger<T> {
        // A negative exponent has no integer result; reject it rather than let the squaring loop fall through to a
        // meaningless value (unsigned T is never negative, so this guard costs a folded-away branch there).
        ArgumentOutOfRangeException.ThrowIfNegative(value: exponent);

        var result = T.One;

        while (true) {
            if (T.IsOddInteger(value: exponent)) {
                result *= value;
            }

            exponent >>= 1;

            if (T.Zero == exponent) {
                return result;
            }

            value *= value;
        }
    }
    /// <summary>Returns a value containing only the lowest set bit of <paramref name="value"/>.</summary>
    /// <typeparam name="T">The binary integer type.</typeparam>
    /// <param name="value">The value to operate on.</param>
    /// <returns>A value in which only the least significant set bit of <paramref name="value"/> is set, or zero when <paramref name="value"/> is zero.</returns>
    public static T LowestSetBit<T>(this T value) where T : IBinaryInteger<T> =>
        value & (-value);
    /// <summary>Clears the contiguous run of set bits at and below the lowest clear (zero) bit of <paramref name="value"/>.</summary>
    /// <typeparam name="T">The binary integer type.</typeparam>
    /// <param name="value">The value to operate on.</param>
    /// <returns><paramref name="value"/> with its trailing run of set bits turned off. This is the bitwise dual of <see cref="FillFromLowestSetBit{T}(T)"/>.</returns>
    public static T FillFromLowestClearBit<T>(this T value) where T : IBinaryInteger<T> =>
        value & (value + T.One);
    /// <summary>Sets every bit below the lowest set bit of <paramref name="value"/>, filling its trailing zeros with ones.</summary>
    /// <typeparam name="T">The binary integer type.</typeparam>
    /// <param name="value">The value to operate on.</param>
    /// <returns><paramref name="value"/> with all bits below its least significant set bit turned on; an all-ones value when <paramref name="value"/> is zero.</returns>
    public static T FillFromLowestSetBit<T>(this T value) where T : IBinaryInteger<T> =>
        value | (value - T.One);
    /// <summary>Divides <paramref name="value"/> by <paramref name="divisor"/> and returns the floored quotient together with the matching floored remainder.</summary>
    /// <typeparam name="T">The binary integer type.</typeparam>
    /// <param name="value">The dividend.</param>
    /// <param name="divisor">The divisor.</param>
    /// <returns>The quotient rounded toward negative infinity and the remainder that carries the sign of <paramref name="divisor"/>, satisfying <c>value == (Quotient * divisor) + Remainder</c>.</returns>
    /// <remarks>Both halves come out of a single division and a single shared correction, so this is the right shape when a caller wants the pair — <see cref="FloorDivide{T}(T, T)"/> followed by <see cref="FloorModulo{T}(T, T)"/> would divide twice.</remarks>
    /// <exception cref="DivideByZeroException"><paramref name="divisor"/> is zero.</exception>
    /// <exception cref="OverflowException"><paramref name="value"/> is the signed minimum and <paramref name="divisor"/> is <c>-1</c>; the underlying division rejects that one pair because its quotient is unrepresentable.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (T Quotient, T Remainder) FloorDivRem<T>(this T value, T divisor) where T : IBinaryInteger<T> {
        var (quotient, remainder) = T.DivRem(
            left: value,
            right: divisor
        );

        // One condition drives both halves: the decrement that lowers the quotient is the same event that adds the divisor
        // back to the remainder, so the pair stays consistent. The addend never overflows — it only fires when the two
        // have opposing signs, so their sum shrinks toward zero.
        var borrow = remainder.IsNonZero() & T.IsNegative(value: remainder ^ divisor).As<T>();

        return ((quotient - borrow), (remainder + (divisor & (-borrow))));
    }
    /// <summary>Divides <paramref name="value"/> by <paramref name="divisor"/> with a quotient rounded toward negative infinity.</summary>
    /// <typeparam name="T">The binary integer type.</typeparam>
    /// <param name="value">The dividend.</param>
    /// <param name="divisor">The divisor.</param>
    /// <returns>The largest value that is no greater than the exact quotient. This differs from the built-in <c>/</c> — which truncates toward zero — only when the division is inexact and the operands carry opposing signs.</returns>
    /// <remarks>The correction is branchless: the truncated quotient is nudged by a single conditional decrement. This is the quotient that pairs with <see cref="FloorModulo{T}(T, T)"/>; <see cref="FloorDivRem{T}(T, T)"/> produces both halves from one division. An unsigned <typeparamref name="T"/> is never signed opposite its divisor, so the correction folds away to nothing there.</remarks>
    /// <exception cref="DivideByZeroException"><paramref name="divisor"/> is zero.</exception>
    /// <exception cref="OverflowException"><paramref name="value"/> is the signed minimum and <paramref name="divisor"/> is <c>-1</c>; the underlying division rejects that one pair because its quotient is unrepresentable.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T FloorDivide<T>(this T value, T divisor) where T : IBinaryInteger<T> {
        var (quotient, remainder) = T.DivRem(
            left: value,
            right: divisor
        );

        // The exact quotient sits below the truncated one exactly when the discarded fraction was negative — a non-zero
        // remainder signed OPPOSITE the divisor. XOR reads both sign bits in one operation.
        var borrow = remainder.IsNonZero() & T.IsNegative(value: remainder ^ divisor).As<T>();

        return (quotient - borrow);
    }
    /// <summary>Reduces <paramref name="value"/> modulo <paramref name="modulus"/> with a floored quotient, so the result carries the sign of <paramref name="modulus"/> rather than of <paramref name="value"/>.</summary>
    /// <typeparam name="T">The binary integer type.</typeparam>
    /// <param name="value">The dividend.</param>
    /// <param name="modulus">The divisor whose sign the result follows; for a positive <paramref name="modulus"/> the result is the non-negative remainder in <c>[0, <paramref name="modulus"/>)</c>, which is what makes this the right tool for wrapping a signed index or offset back into range.</param>
    /// <returns>The floored remainder — the unique value congruent to <paramref name="value"/> modulo <paramref name="modulus"/> that lies on the same side of zero as <paramref name="modulus"/>. This differs from the built-in <c>%</c> only when the operands carry opposing signs.</returns>
    /// <remarks>The correction is branchless: the built-in truncated remainder is nudged by a single conditional addition of <paramref name="modulus"/>, never the <c>((value % modulus) + modulus) % modulus</c> double reduction — which both spends a second division and can overflow its intermediate sum (for a large positive <paramref name="modulus"/> that double reduction is not merely slower but wrong). An unsigned <typeparamref name="T"/> is already non-negative, so the correction folds away to nothing there.</remarks>
    /// <exception cref="DivideByZeroException"><paramref name="modulus"/> is zero.</exception>
    /// <exception cref="OverflowException"><paramref name="value"/> is the signed minimum and <paramref name="modulus"/> is <c>-1</c>; the underlying remainder rejects that one pair because its quotient is unrepresentable.</exception>
    public static T FloorModulo<T>(this T value, T modulus) where T : IBinaryInteger<T> {
        var remainder = (value % modulus);

        // The truncated remainder carries the sign of `value`; add `modulus` back exactly when the remainder is
        // non-zero and signed opposite the modulus, landing it on the divisor's side. The addend never overflows:
        // it only fires when the two have opposing signs, so their sum shrinks toward zero.
        var wrap = remainder.IsNonZero() & T.IsNegative(value: remainder ^ modulus).As<T>();

        return (remainder + (modulus & (-wrap)));
    }
    /// <summary>Computes the greatest common divisor of <paramref name="value"/> and <paramref name="other"/>.</summary>
    /// <typeparam name="T">The binary integer type.</typeparam>
    /// <param name="value">The first operand; its magnitude is used.</param>
    /// <param name="other">The second operand; its magnitude is used.</param>
    /// <returns>The largest value that divides both operands. When one operand is zero, the magnitude of the other is returned.</returns>
    /// <exception cref="OverflowException">The result is the magnitude of the minimum signed value and is not representable by <typeparamref name="T"/>.</exception>
    /// <remarks>Implemented with the binary GCD (Stein's) algorithm, whose inner loop is branchless.</remarks>
    public static T GreatestCommonDivisor<T>(this T value, T other) where T : IBinaryInteger<T> {
        // The zero fast paths return the OTHER operand's magnitude, matching the general path below (which abs-es both):
        // returning it verbatim would leak a negative sign the contract promises to drop.
        if (T.Zero == other) { return T.Abs(value: value); } else if (T.Zero == value) { return T.Abs(value: other); }

        // A signed minimum has no positive magnitude in T. If exactly one operand is the minimum, its magnitude is a
        // power of two, so the GCD is simply the lowest set bit of the other operand's magnitude and is representable.
        // If both operands are the minimum, T.Abs below deliberately reports the unrepresentable result.
        var valueIsMinimum = (T.IsNegative(value: value) && (value == -value));
        var otherIsMinimum = (T.IsNegative(value: other) && (other == -other));

        if (valueIsMinimum != otherIsMinimum) {
            var magnitude = T.Abs(value: (valueIsMinimum
                ? other
                : value));

            return (T.One << int.CreateChecked(value: T.TrailingZeroCount(value: magnitude)));
        }

        other = T.Abs(value: other);
        value = T.Abs(value: value);

        var shift = int.CreateTruncating(value: T.TrailingZeroCount(value: other | value));

        other >>= int.CreateTruncating(value: T.TrailingZeroCount(value: other));
        value >>= int.CreateTruncating(value: T.TrailingZeroCount(value: value));

        if (other != value) {
            do {
                var swap = (other ^ value) & (-(value < other).As<T>());

                other ^= swap;
                value ^= swap;
                value -= other;
                value >>= int.CreateTruncating(value: T.TrailingZeroCount(value: value));
            } while (other != value);
        }

        return (other << shift);
    }
    /// <summary>Computes the least common multiple of <paramref name="value"/> and <paramref name="other"/>.</summary>
    /// <typeparam name="T">The binary integer type.</typeparam>
    /// <param name="value">The first operand.</param>
    /// <param name="other">The second operand.</param>
    /// <returns>The smallest value that is a multiple of both operands, or zero when either operand is zero. The result wraps on overflow.</returns>
    /// <remarks>The quotient is divided out before multiplying (<c>(value / gcd) * other</c>) to reduce the chance of overflow.</remarks>
    public static T LeastCommonMultiple<T>(this T value, T other) where T : IBinaryInteger<T> {
        var divisor = value.GreatestCommonDivisor(other: other);

        if (T.Zero == divisor) { return T.Zero; }

        var quotient = (value / divisor);
        var result = (quotient * other);

        // Remove only the MATHEMATICAL sign, based on the operands before multiplication. Looking at the wrapped
        // product's sign would mistake an overflowed positive result for a negative input and change its modular bits.
        return ((T.IsNegative(value: quotient) != T.IsNegative(value: other))
            ? -result
            : result
        );
    }
    /// <summary>Returns the least significant base-10 digit of <paramref name="value"/>.</summary>
    /// <typeparam name="T">The binary integer type.</typeparam>
    /// <param name="value">The value to examine; its sign is ignored.</param>
    /// <returns>The ones digit of <paramref name="value"/>, a value from <c>0</c> through <c>9</c>.</returns>
    public static T LeastSignificantDigit<T>(this T value) where T : IBinaryInteger<T> =>
        // Abs the single-digit remainder, not the whole value, so T.MinValue yields its ones digit instead of throwing.
        T.Abs(value: (value % BinaryIntegerConstants<T>.Ten));
    /// <summary>Returns the number of base-10 digits required to represent the magnitude of <paramref name="value"/>.</summary>
    /// <typeparam name="T">The binary integer type.</typeparam>
    /// <param name="value">The value to measure; its sign is ignored.</param>
    /// <returns>The decimal digit count of <paramref name="value"/>. For a non-zero magnitude this equals <c>⌊log₁₀(|value|)⌋ + 1</c>; the magnitude zero yields <c>1</c>.</returns>
    public static T DigitCount<T>(this T value) where T : IBinaryInteger<T> {
        if (T.IsZero(value: value)) { return T.One; }

        // From the bit length: 1233/4096 sits just below log₁₀ 2, so estimate = ⌊(bitLength − 1)·1233/4096⌋ never exceeds
        // ⌊log₁₀|value|⌋ and falls short of it by at most one. 10^estimate is therefore at most |value| and always
        // representable; whether |value| also reaches 10^(estimate + 1) is read off the truncating quotient by
        // 10^estimate, which keeps both the SIGNED value (T.MinValue has no T.Abs) and the carrier's own ceiling
        // (10^(estimate + 1) may not fit) out of the decision.
        var bitLength = int.CreateChecked(value: ((value < T.Zero)
            ? (~value).BitLength()
            : value.BitLength()));
        var estimate = T.CreateChecked(value: (((bitLength - 1) * 1233) >> 12));
        // The quotient's magnitude is below one hundred, so negating it is always representable — unlike a negated ten
        // on an unsigned carrier, which would wrap to the carrier's top and admit every quotient.
        var quotient = (value / BinaryIntegerConstants<T>.Ten.Exponentiate(exponent: estimate));
        var magnitude = (T.IsNegative(value: quotient)
            ? -quotient
            : quotient
        );

        return ((magnitude >= BinaryIntegerConstants<T>.Ten)
            ? (estimate + (T.One + T.One))
            : (estimate + T.One)
        );
    }
    /// <summary>Gathers the bits of <paramref name="value"/> at the set positions of <paramref name="mask"/> into the low bits of the result, the operation x86 names <c>PEXT</c>.</summary>
    /// <typeparam name="T">The fixed-width binary integer type, signed or unsigned.</typeparam>
    /// <param name="value">The source whose selected bits are gathered.</param>
    /// <param name="mask">The source positions; any bit pattern, including a negative signed value.</param>
    /// <returns>A value whose bit <c>k</c> is the bit of <paramref name="value"/> at the <c>k</c>-th lowest set position of <paramref name="mask"/>, with every bit from the mask's population count upward clear.</returns>
    /// <remarks>
    /// One <c>PEXT</c> serves a carrier up to 64 bits wide and two serve a 128-bit carrier when BMI2 is available and fast
    /// (<see cref="BitManipulation.HasFastParallelBits"/>); otherwise a loop visits the mask's set bits from the bottom. Both return the same bits.
    /// <see cref="ParallelBitDeposit{T}(T, T)"/> is the inverse on the positions <paramref name="mask"/> selects.
    /// </remarks>
    /// <exception cref="NotSupportedException"><typeparamref name="T"/> is <see cref="BigInteger"/>, which has no fixed word width.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T ParallelBitExtract<T>(this T value, T mask) where T : IBinaryInteger<T> {
        BinaryIntegerConstants<T>.ThrowIfUnbounded(operationName: nameof(ParallelBitExtract));

        return (HasHardwareBitScatter<T>()
            ? ParallelBitExtractInHardware(
                mask: mask,
                value: value
            )
            : ParallelBitExtractInSoftware(
                mask: mask,
                value: value
            ));
    }
    /// <summary>Builds the word whose low <paramref name="count"/> bits are set and whose remaining bits are clear.</summary>
    /// <typeparam name="T">The binary integer type the mask is produced in.</typeparam>
    /// <param name="count">The number of low ones, from zero through the bit width of <typeparamref name="T"/>; a <see cref="BigInteger"/> accepts any non-negative count.</param>
    /// <returns><c>2^count - 1</c> as a bit pattern: zero when <paramref name="count"/> is zero, and the all-ones word (minus one for a signed carrier) when it equals the carrier width.</returns>
    /// <remarks>Exact at both ends, where <c>(1 &lt;&lt; count) - 1</c> fails at the full width because the shift count wraps modulo the width. BMI2's <c>BZHI</c> builds it in one instruction for carriers up to 64 bits.</remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative or exceeds the bit width of <typeparamref name="T"/>.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T LowMask<T>(this int count) where T : IBinaryInteger<T> {
        if (BinaryIntegerConstants<T>.IsUnbounded
            ? (count < 0)
            : (((uint)count) > ((uint)(Unsafe.SizeOf<T>() << 3)))) { ThrowLowMaskCount(); }

        return LowMaskInRange<T>(count: count);
    }
    /// <summary>
    /// Builds the periodic bit mask whose set bits form blocks of <c>2^<paramref name="exponent"/></c> ones alternating
    /// with equally sized blocks of zeros (for example <c>0x5555…</c>, <c>0x3333…</c>, and <c>0x0F0F…</c> for exponents
    /// <c>0</c>, <c>1</c>, and <c>2</c>).
    /// </summary>
    /// <typeparam name="T">The fixed-width binary integer type the mask is produced in, signed or unsigned.</typeparam>
    /// <param name="exponent">The block exponent, from zero through <c>log₂(width) - 1</c>, so that two blocks of <c>2^exponent</c> bits fit in the word.</param>
    /// <returns>The repeating mask for the requested block width; the largest exponent yields the low half of the word.</returns>
    /// <remarks>
    /// Repeats a block of ones followed by an equally wide block of zeros through <see cref="RepeatBits{T}(T, int)"/>.
    /// For b-bit blocks in a W-bit word, <c>(2^b - 1) * (2^W - 1) / (2^(2b) - 1)</c> simplifies to
    /// <c>(2^W - 1) / (2^b + 1)</c>, the Fermat-number construction. These masks drive the SWAR bit-permutation
    /// routines such as <see cref="BitwisePair{TInput, TResult}(TInput, TInput)"/> and <see cref="ReverseBits{T}(T)"/>.
    /// </remarks>
    /// <exception cref="NotSupportedException"><typeparamref name="T"/> is <see cref="BigInteger"/>, which has no fixed word width.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="exponent"/> is negative, or two blocks of <c>2^exponent</c> bits do not fit in the word.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T NthFermatMask<T>(this int exponent) where T : IBinaryInteger<T> {
        BinaryIntegerConstants<T>.ThrowIfUnbounded(operationName: nameof(NthFermatMask));

        if (((uint)exponent) >= ((uint)BitOperations.Log2(value: ((uint)(Unsafe.SizeOf<T>() << 3))))) { ThrowExponent(); }

        var blockWidth = (1 << exponent);

        if (
            (typeof(T) == typeof(UInt128)) ||
            (typeof(T) == typeof(Int128))
        ) {
            return RepeatWordInto128<T>(
                blockWidth: (blockWidth << 1),
                value: (ulong.MaxValue >>> (64 - blockWidth))
            );
        }

        // A legal block occupies at most half the word. Up through 128-bit carriers its ones therefore fit in a
        // ulong; materializing that scalar first avoids spending the caller's inline budget on wide shifts/subtracts.
        var pattern = ((Unsafe.SizeOf<T>() <= 16)
            ? T.CreateTruncating(value: (ulong.MaxValue >>> (64 - blockWidth)))
            : ((T.One << blockWidth) - T.One)
        );

        return pattern.RepeatBits(blockWidth: (blockWidth << 1));
    }
    /// <summary>Computes two raised to the power <paramref name="exponent"/>: the word whose only set bit is bit <paramref name="exponent"/>.</summary>
    /// <typeparam name="T">The binary integer type the result is produced in.</typeparam>
    /// <param name="exponent">The exponent, from zero through the bit width of <typeparamref name="T"/> minus one; a <see cref="BigInteger"/> accepts any non-negative exponent.</param>
    /// <returns><c>2^exponent</c> as a bit pattern. For a signed carrier the top exponent sets the sign bit alone and yields the signed minimum.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="exponent"/> is negative or not below the bit width of <typeparamref name="T"/>.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T NthPowerOfTwo<T>(this int exponent) where T : IBinaryInteger<T> {
        if (BinaryIntegerConstants<T>.IsUnbounded
            ? (exponent < 0)
            : (((uint)exponent) >= ((uint)(Unsafe.SizeOf<T>() << 3)))) { ThrowExponent(); }

        return (T.One << exponent);
    }
    /// <summary>Returns the most significant (leading) base-10 digit of <paramref name="value"/>.</summary>
    /// <typeparam name="T">The binary integer type.</typeparam>
    /// <param name="value">The value to examine; its sign is ignored.</param>
    /// <returns>The leading decimal digit of <paramref name="value"/>, a value from <c>0</c> through <c>9</c>.</returns>
    public static T MostSignificantDigit<T>(this T value) where T : IBinaryInteger<T> =>
        // Divide the signed value by the leading power of ten, then abs the single-digit result: |value / p| equals
        // |value| / p (p is positive), so this avoids the unrepresentable T.Abs(T.MinValue).
        T.Abs(value: (value / BinaryIntegerConstants<T>.Ten.Exponentiate(exponent: (value.DigitCount() - T.One))));
    /// <summary>Advances <paramref name="value"/> to the next bit permutation with the same population count of its finite bit content.</summary>
    /// <typeparam name="T">The binary integer type.</typeparam>
    /// <param name="value">The current bit permutation.</param>
    /// <returns>
    /// For a fixed-width carrier, the next same-population-count carrier bit pattern in unsigned lexicographic order:
    /// the terminal pattern wraps to the lowest pattern, while zero and the all-ones pattern map to themselves. For a
    /// <see cref="BigInteger"/>, the neighbouring value whose infinite two's-complement string carries the same number
    /// of minority bits: non-negative values ascend, negative values descend, and zero and minus one map to themselves.
    /// </returns>
    /// <remarks>
    /// This is the classic next-bit-permutation operation (Gosper's hack), made cyclic on fixed-width carriers. Signed
    /// fixed-width values are treated as raw two's-complement carrier bits; their numeric sign does not restrict the
    /// operation. A <see cref="BigInteger"/> carries no width, so it is read as what it is — the infinite string whose
    /// finitely many minority bits differ from its constant sign tail. Every class with a nonzero minority count is
    /// therefore infinite and never wraps, no two values share a successor, and iteration never migrates between
    /// classes. Register semantics — closed cycles, terminal wraps — are exclusively a fixed-width-carrier property.
    /// </remarks>
    /// <exception cref="OverflowException">A <see cref="BigInteger"/> walk step needs a shift count above
    /// <see cref="int.MaxValue"/>; only values carrying more than two billion trailing zeros — or trailing ones, when
    /// negative — reach this.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T PermuteBitsLexicographically<T>(this T value) where T : IBinaryInteger<T> {
        // This test is constant for every closed T. The JIT discards the BigInteger arm for every fixed-width
        // instantiation, leaving the original direct generic-math path with no unbounded-carrier overhead.
        if (typeof(T) == typeof(BigInteger)) {
            var bigInteger = Unsafe.As<T, BigInteger>(source: ref value);
            var bigIntegerResult = PermuteBigIntegerBits(value: bigInteger);

            return Unsafe.As<BigInteger, T>(source: ref bigIntegerResult);
        }

        var populationCount = int.CreateChecked(value: T.PopCount(value: value));
        var x = value.FillFromLowestSetBit();
        var y = int.CreateTruncating(value: (T.TrailingZeroCount(value: value) + T.One));
        var z = (((~x).LowestSetBit() - T.One) >>> y);
        var result = (x + T.One) | z;

        if (T.PopCount(value: result) == T.CreateChecked(value: populationCount)) {
            return result;
        }

        // The count is at most the width, so the whole-word class wraps to all ones rather than a shift by zero.
        return LowMaskInRange<T>(count: populationCount);
    }
    /// <summary>Returns the parity of the population count of <paramref name="value"/>.</summary>
    /// <typeparam name="T">The binary integer type.</typeparam>
    /// <param name="value">The value to examine.</param>
    /// <returns><c>1</c> when <paramref name="value"/> has an odd number of set bits; otherwise <c>0</c>.</returns>
    public static T PopulationParity<T>(this T value) where T : IBinaryInteger<T> =>
        T.PopCount(value: value) & T.One;
    /// <summary>Converts a reflected binary (Gray) code back to its standard binary representation.</summary>
    /// <typeparam name="T">The binary integer type.</typeparam>
    /// <param name="value">The Gray-coded value to decode.</param>
    /// <returns>The standard binary value corresponding to the Gray code <paramref name="value"/>.</returns>
    /// <remarks>This is the inverse of <see cref="ReflectedBinaryEncode{T}(T)"/>.</remarks>
    /// <exception cref="NotSupportedException"><typeparamref name="T"/> is <see cref="BigInteger"/>. Decoding is width-bounded — it XOR-folds against the carrier's own bit width.</exception>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static T ReflectedBinaryDecode<T>(this T value) where T : IBinaryInteger<T> {
        BinaryIntegerConstants<T>.ThrowIfUnbounded(operationName: nameof(ReflectedBinaryDecode));

        // A prefix XOR from the top: each rung doubles the span already folded in.
        return ClimbButterfly<T, PrefixXorRung<T>>(
            levelCount: BitOperations.Log2(value: ((uint)(Unsafe.SizeOf<T>() << 3))),
            value: value
        );
    }
    /// <summary>Converts a standard binary value to its reflected binary (Gray) code.</summary>
    /// <typeparam name="T">The binary integer type.</typeparam>
    /// <param name="value">The value to encode.</param>
    /// <returns>The Gray code of <paramref name="value"/>, in which successive integers differ by exactly one bit.</returns>
    /// <remarks><see cref="ReflectedBinaryDecode{T}(T)"/> recovers the original value.</remarks>
    public static T ReflectedBinaryEncode<T>(this T value) where T : IBinaryInteger<T> =>
        value ^ (value >>> 1);
    /// <summary>Repeats the low <paramref name="blockWidth"/> bits of <paramref name="value"/> across a fixed-width word.</summary>
    /// <typeparam name="T">The fixed-width binary integer type, signed or unsigned.</typeparam>
    /// <param name="value">The pattern, with no set bits outside its block. A whole-word block accepts every bit pattern, including negative values.</param>
    /// <param name="blockWidth">The block width, from one through the bit width of <typeparamref name="T"/>. A width that does not divide the word leaves the last copy truncated at the top.</param>
    /// <returns>The repeated pattern; for example, <c>0xABu.RepeatBits(8)</c> is <c>0xABABABAB</c> and <c>0b011u.RepeatBits(3)</c> is <c>0xDB6DB6DB</c>.</returns>
    /// <remarks>
    /// Multiplying the pattern by <see cref="ReplicationMask{T}(int)"/> places one copy in each block without
    /// overlapping bits, and the product's truncation cuts the last copy. For <see cref="UInt128"/> and
    /// <see cref="Int128"/>, a block dividing 64 repeats within a <see cref="ulong"/> first and that word is copied into
    /// both halves, avoiding the wide multiplication; whole-word blocks return unchanged. The result is a bit pattern,
    /// so a signed result may be negative.
    /// </remarks>
    /// <exception cref="NotSupportedException"><typeparamref name="T"/> is <see cref="BigInteger"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="blockWidth"/> is not positive or exceeds the word width, or <paramref name="value"/> has set bits outside the block.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T RepeatBits<T>(this T value, int blockWidth) where T : IBinaryInteger<T> {
        var bitWidth = ValidateReplicationBlockWidth<T>(blockWidth: blockWidth);

        if (blockWidth == bitWidth) { return value; }

        if (
            (
                (typeof(T) == typeof(UInt128)) ||
                (typeof(T) == typeof(Int128))
            ) &&
            ((64 % blockWidth) == 0)
        ) {
            if ((value >>> 64) != T.Zero) { ThrowReplicationPattern(); }

            // A block dividing 64 fits in a ulong. Repeat within it and copy the word, avoiding the wide
            // multiplication that otherwise survives even when both operands are constants.
            return RepeatWordInto128<T>(
                value: ulong.CreateTruncating(value: value),
                blockWidth: blockWidth
            );
        }

        if ((value & ~(T.AllBitsSet >>> (bitWidth - blockWidth))) != T.Zero) { ThrowReplicationPattern(); }

        return unchecked((value * blockWidth.ReplicationMask<T>()));
    }
    /// <summary>Builds a word with one set bit at the bottom of every block of <paramref name="blockWidth"/> bits.</summary>
    /// <typeparam name="T">The fixed-width binary integer type, signed or unsigned.</typeparam>
    /// <param name="blockWidth">The block width, from one through the bit width of <typeparamref name="T"/>. A width that does not divide the word still marks its last, truncated block.</param>
    /// <returns>The replication mask: bit <c>i * blockWidth</c> set for every block that starts inside the word. For example, <c>8.ReplicationMask&lt;uint&gt;()</c> is <c>0x01010101</c> and <c>3.ReplicationMask&lt;byte&gt;()</c> is <c>0x49</c>.</returns>
    /// <remarks>
    /// For a W-bit word and b-bit blocks, the geometric series <c>1 + 2^b + 2^(2b) + ...</c> equals
    /// <c>(2^W - 1) / (2^b - 1)</c> when b divides W. Otherwise, with <c>W = qb + r</c>, the floor of that quotient
    /// holds q marks sitting r bits above the block boundaries; shifting it left by <c>b - r</c> moves them onto the
    /// boundaries from b through the truncated block's at <c>qb</c>, and the mark at bit zero is set separately. A
    /// signed result carries the same bits as its unsigned
    /// counterpart; in particular, one-bit blocks return an all-ones word. No shift by the whole word width is
    /// performed.
    /// </remarks>
    /// <exception cref="NotSupportedException"><typeparamref name="T"/> is <see cref="BigInteger"/>, which has no fixed word width.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="blockWidth"/> is not positive or exceeds the word width.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T ReplicationMask<T>(this int blockWidth) where T : IBinaryInteger<T> {
        var bitWidth = ValidateReplicationBlockWidth<T>(blockWidth: blockWidth);

        if (blockWidth == bitWidth) { return T.One; }

        if (
            (typeof(T) == typeof(UInt128)) ||
            (typeof(T) == typeof(Int128))
        ) {
            // Build each half from machine words; the JIT folds ulong division by constants, whereas Int128/UInt128
            // division remains a runtime call. The upper half's first mark sits where the first block at or above bit
            // 64 begins, so a block dividing 64 marks both halves identically.
            if (blockWidth >= 64) {
                return T.CreateTruncating(value: new UInt128(
                    lower: 1UL,
                    upper: (1UL << (blockWidth - 64))
                ));
            }

            var lower = blockWidth.ReplicationMask<ulong>();

            return T.CreateTruncating(value: new UInt128(
                lower: lower,
                upper: (lower << ((blockWidth - 1) - (63 % blockWidth)))
            ));
        }

        var allBits = T.AllBitsSet;
        var signed = T.IsNegative(value: allBits).As<int>();
        var divisor = ((T.One << blockWidth) - T.One);

        // The all-ones numerator is odd, so halving it for a signed T floors the quotient to half its unsigned value;
        // the quotient itself is odd exactly when the block divides the word. The shift restores the halved bit and
        // lifts the marks of a non-dividing block by b - r, where (b - 1) - ((W - 1) mod b) is b - r reduced modulo b;
        // the final OR sets bit zero.
        return (((allBits >>> signed) / divisor) << (signed + ((blockWidth - 1) - ((bitWidth - 1) % blockWidth)))) | T.One;
    }
    /// <summary>Returns <paramref name="value"/> with the order of all of its bits reversed.</summary>
    /// <typeparam name="T">The binary integer type.</typeparam>
    /// <param name="value">The value whose bits are reversed.</param>
    /// <returns>A value whose bit at position <c>i</c> equals the bit of <paramref name="value"/> at position <c>(width − 1 − i)</c>.</returns>
    /// <remarks>
    /// Arm64 reverses a 32- or 64-bit word in one instruction. Elsewhere a SWAR ladder reverses the bits within each
    /// byte and a byte swap reverses the byte order; a single byte closes its ladder with a nibble rotation instead.
    /// </remarks>
    /// <exception cref="NotSupportedException"><typeparamref name="T"/> is <see cref="BigInteger"/>. Bit reversal requires a fixed carrier width to define which bit is "first".</exception>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static T ReverseBits<T>(this T value) where T : IBinaryInteger<T> {
        BinaryIntegerConstants<T>.ThrowIfUnbounded(operationName: nameof(ReverseBits));

        // One RBIT instruction on Arm64 for the two machine widths; the reversal is exact, so it returns the same bits
        // the ladder below does.
        if (ArmBase.Arm64.IsSupported) {
            if (
                (typeof(T) == typeof(ulong)) ||
                (typeof(T) == typeof(long))
            ) {
                return T.CreateTruncating(value: ArmBase.Arm64.ReverseElementBits(value: ulong.CreateTruncating(value: value)));
            }

            if (
                (typeof(T) == typeof(uint)) ||
                (typeof(T) == typeof(int))
            ) {
                return T.CreateTruncating(value: ArmBase.ReverseElementBits(value: uint.CreateTruncating(value: value)));
            }
        }

        if (Unsafe.SizeOf<T>() == sizeof(byte)) {
            value = ClimbButterfly<T, SwapRung<T>>(
                levelCount: 2,
                value: value
            );

            return (value >>> 4) | (value << 4);
        }

        // Three rungs reverse the bits within every byte; the byte swap finishes the permutation.
        value = ClimbButterfly<T, SwapRung<T>>(
            levelCount: 3,
            value: value
        );

        return Unsafe.SizeOf<T>() switch {
            sizeof(ushort) => T.CreateTruncating(value: BinaryPrimitives.ReverseEndianness(value: ushort.CreateTruncating(value: value))),
            sizeof(uint) => T.CreateTruncating(value: BinaryPrimitives.ReverseEndianness(value: uint.CreateTruncating(value: value))),
            sizeof(ulong) => T.CreateTruncating(value: BinaryPrimitives.ReverseEndianness(value: ulong.CreateTruncating(value: value))),
            _ => T.CreateTruncating(value: BinaryPrimitives.ReverseEndianness(value: UInt128.CreateTruncating(value: value))),
        };
    }
    /// <summary>Returns <paramref name="value"/> with the order of its base-10 digits reversed, preserving the sign.</summary>
    /// <typeparam name="T">The binary integer type.</typeparam>
    /// <param name="value">The value whose decimal digits are reversed.</param>
    /// <returns><paramref name="value"/> with its decimal digits reversed (for example <c>1230</c> becomes <c>321</c>), carrying the original sign. The result wraps on overflow.</returns>
    public static T ReverseDigits<T>(this T value) where T : IBinaryInteger<T> {
        // Reverse the SIGNED value directly: signed DivRem yields sign-carrying remainders, so the accumulated result
        // already bears the original sign (no CopySign, no unrepresentable T.Abs(T.MinValue)).
        var quotient = value;
        var result = T.Zero;

        do {
            (quotient, var remainder) = T.DivRem(
                left: quotient,
                right: BinaryIntegerConstants<T>.Ten
            );

            result = ((result * BinaryIntegerConstants<T>.Ten) + remainder);
        } while (T.Zero != quotient);

        return result;
    }
    /// <summary>Cyclically rotates the base-10 digits of <paramref name="value"/> toward the most significant end, preserving the sign.</summary>
    /// <typeparam name="T">The binary integer type.</typeparam>
    /// <param name="value">The value whose decimal digits are rotated.</param>
    /// <param name="count">The number of digit positions to rotate left; the count is reduced modulo the digit count.</param>
    /// <returns><paramref name="value"/> with its decimal digits rotated left by <paramref name="count"/> places, carrying the original sign.</returns>
    public static T RotateDigitsLeft<T>(this T value, int count) where T : IBinaryInteger<T> =>
        value.RotateDigits(count: count);
    /// <summary>Cyclically rotates the base-10 digits of <paramref name="value"/> toward the least significant end, preserving the sign.</summary>
    /// <typeparam name="T">The binary integer type.</typeparam>
    /// <param name="value">The value whose decimal digits are rotated.</param>
    /// <param name="count">The number of digit positions to rotate right; the count is reduced modulo the digit count.</param>
    /// <returns><paramref name="value"/> with its decimal digits rotated right by <paramref name="count"/> places, carrying the original sign.</returns>
    /// <remarks>
    /// <paramref name="count"/> is widened to <see cref="long"/> before negating: negating <see cref="int.MinValue"/> as
    /// an <see cref="int"/> wraps back to itself (its magnitude, <c>2147483648</c>, is not representable in <see cref="int"/>),
    /// which would silently rotate by the original, un-negated count instead of by its true magnitude in the opposite
    /// direction.
    /// </remarks>
    public static T RotateDigitsRight<T>(this T value, int count) where T : IBinaryInteger<T> =>
        value.RotateDigits(count: -((long)count));
    /// <summary>Sets every bit below the highest set bit of <paramref name="value"/>, filling from its leading one down to bit zero.</summary>
    /// <typeparam name="T">The fixed-width binary integer type.</typeparam>
    /// <param name="value">The value to operate on.</param>
    /// <returns>The all-ones word of <paramref name="value"/>'s <see cref="BitLength{T}(T)"/>: zero when <paramref name="value"/> is zero, and all ones for a negative signed value, whose sign bit is its highest set bit. This is the dual of <see cref="FillFromLowestSetBit{T}(T)"/>.</returns>
    /// <exception cref="NotSupportedException"><typeparamref name="T"/> is <see cref="BigInteger"/>, whose negative values have no highest set bit.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T SmearBelowHighestSetBit<T>(this T value) where T : IBinaryInteger<T> {
        BinaryIntegerConstants<T>.ThrowIfUnbounded(operationName: nameof(SmearBelowHighestSetBit));

        return LowMaskInRange<T>(count: int.CreateTruncating(value: value.BitLength()));
    }
    /// <summary>Adds two values, reporting whether the exact sum is representable in <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">The binary integer type.</typeparam>
    /// <param name="left">The first addend.</param>
    /// <param name="right">The second addend.</param>
    /// <param name="sum">The exact sum when it is representable; otherwise the wrapped sum.</param>
    /// <returns><see langword="true"/> when the addition did not overflow. A signed sum overflows exactly when both
    /// addends share a sign the wrapped sum does not; an unsigned sum overflows exactly when it wraps below an addend.
    /// A <see cref="BigInteger"/> sum never overflows.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryAdd<T>(this T left, T right, out T sum) where T : IBinaryInteger<T> {
        sum = unchecked((left + right));

        return (T.IsNegative(value: T.AllBitsSet)
            ? !T.IsNegative(value: (left ^ sum) & (right ^ sum))
            : (sum >= left)
        );
    }
    /// <summary>Converts a value to another binary integer type when, and only when, the conversion is exact.</summary>
    /// <typeparam name="TWide">The source type.</typeparam>
    /// <typeparam name="TNarrow">The destination type.</typeparam>
    /// <param name="value">The value to convert.</param>
    /// <param name="result">The converted value when <paramref name="value"/> is representable in
    /// <typeparamref name="TNarrow"/>; otherwise zero.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> lies in <typeparamref name="TNarrow"/>'s range, so
    /// <paramref name="result"/> denotes the same integer. Nothing is wrapped or clamped: an out-of-range value is
    /// refused.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryNarrow<TWide, TNarrow>(this TWide value, out TNarrow result) where TWide : IBinaryInteger<TWide> where TNarrow : IBinaryInteger<TNarrow> {
        // A saturating conversion is exact exactly when the value is in range; out of range it lands on a destination
        // extreme strictly nearer zero than the value, which the saturating conversion back cannot restore.
        var narrowed = TNarrow.CreateSaturating(value: value);

        if (TWide.CreateSaturating(value: narrowed) != value) {
            result = TNarrow.Zero;

            return false;
        }

        result = narrowed;

        return true;
    }

    /// <summary>One rung of a log-depth SWAR network over <typeparamref name="T"/>: the rung at <c>level</c> moves bit groups <c>2^level</c> wide.</summary>
    /// <typeparam name="T">The binary integer carrier.</typeparam>
    private interface IButterflyRung<T> where T : IBinaryInteger<T> {
        /// <summary>Applies the rung at <paramref name="level"/> of a ladder <paramref name="levelCount"/> rungs tall.</summary>
        static abstract T Apply(T value, int level, int levelCount);
    }
    /// <summary>Gathers the even bits of each <c>2^(level + 2)</c>-bit block into its low half; the inverse of <see cref="SpreadRung{T}"/>.</summary>
    private readonly struct GatherRung<T> : IButterflyRung<T> where T : IBinaryInteger<T> {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Apply(T value, int level, int levelCount) =>
            (value | (value >> level.NthPowerOfTwo<int>())) & (level + 1).NthFermatMask<T>();
    }
    /// <summary>XOR-folds each bit with the one <c>2^level</c> positions above it, a step of the Gray-code prefix sum.</summary>
    private readonly struct PrefixXorRung<T> : IButterflyRung<T> where T : IBinaryInteger<T> {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Apply(T value, int level, int levelCount) =>
            value ^ (value >>> level.NthPowerOfTwo<int>());
    }
    /// <summary>Spreads each block's low half apart from the top rung down, so that after the last rung every source bit sits at an even position.</summary>
    private readonly struct SpreadRung<T> : IButterflyRung<T> where T : IBinaryInteger<T> {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Apply(T value, int level, int levelCount) {
            var exponent = ((levelCount - 1) - level);

            return (value | (value << exponent.NthPowerOfTwo<int>())) & exponent.NthFermatMask<T>();
        }
    }
    /// <summary>Exchanges each pair of neighbouring <c>2^level</c>-bit groups.</summary>
    private readonly struct SwapRung<T> : IButterflyRung<T> where T : IBinaryInteger<T> {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Apply(T value, int level, int levelCount) {
            var mask = level.NthFermatMask<T>();
            var shift = level.NthPowerOfTwo<int>();

            return ((value >>> shift) & mask) | ((value & mask) << shift);
        }
    }
    /// <summary>Gathers every third bit, doubling the packed run each rung; the inverse of <see cref="TriadSpreadRung{T}"/>.</summary>
    private readonly struct TriadGatherRung<T> : IButterflyRung<T> where T : IBinaryInteger<T> {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Apply(T value, int level, int levelCount) {
            var run = (2 << level);

            return (value | (value >>> run)) & TriadMask<T>(width: run);
        }
    }
    /// <summary>Spreads runs apart from the top rung down, so that after the last rung every source bit sits at a multiple of three.</summary>
    private readonly struct TriadSpreadRung<T> : IButterflyRung<T> where T : IBinaryInteger<T> {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Apply(T value, int level, int levelCount) {
            var run = (1 << ((levelCount - 1) - level));

            return (value | (value << (run << 1))) & TriadMask<T>(width: run);
        }
    }
}
