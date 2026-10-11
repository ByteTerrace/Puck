using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Puck.Maths;

public static partial class PrimeDivisionKernels {
    // Leaf quotients floor(n/d) whose divisors are table primes with a stored binary64 reciprocal, so a multiply
    // replaces the division of the DivideGourdonQuotients rungs, each turned at once into its bit position in a U(30)
    // bitmap. Every rung computes the same exact integers; the floating-point product only proposes an estimate that
    // one integer step corrects.
    //
    // Error bound. Let n<2^64, 0<d<2^31 and Q=floor(n/d)<2^48. Converting n to binary64 errs by at most one unit in
    // the last place, a relative error of at most 2^-52. The stored reciprocal r=1.0/d is correctly rounded, at most
    // 2^-53 relative, and so is the product of the two. The estimate e therefore satisfies
    // |e-n/d| <= (n/d)*((1+2^-52)*(1+2^-53)^2-1) < (n/d)*2^-50 < 2^48*2^-50 = 1/4. Adding 2^52 lands in [2^52,2^53),
    // where binary64 integers are spaced by one, so round-to-nearest yields an integer R with |R-e| <= 1/2, and the
    // low 52 bits of the sum's encoding are R itself. Hence |R-n/d| < 3/4 and R is Q or Q+1. The remainder n-R*d lies
    // in [-d,d), within a signed 32-bit value, so its low 32 bits alone are exact: they are those of n-B*d for the
    // sum's whole encoding B, whose exponent bits all lie above bit 51. It is negative exactly when R=Q+1, and
    // subtracting that one unit yields Q.
    //
    // Position. With a=Q-low+1 and b=floor(a/30), the offsets 0 through Q-low coprime to 30 number n=8b+u, where u
    // counts the units below t=a%30. Every unit is odd, so u is PrimeWheel30.UnitsBelowHalves[t>>1], one byte shuffle
    // of a sixteen-entry table. A rung stores (2^29+8)*b+u = 2^32*floor(n/64) + 2^29*(b%8) + 64*floor(n/64) + n%64; for
    // a<2^28 the low 32 bits stay below 8*2^29, so the high half is the word floor(n/64) and the low six bits are the
    // bit n%64, and one multiply packs both.
    //
    // Division by 30. The product P=a*0x88888889 is exact in 64 bits, and 0x88888889*30=2^36+14, so
    // P=b*2^36+F with F=14*a/30+t*2^36/30 below 2^36: P>>36 is b. The fraction's top bits identify t without it:
    // F/2^31=t+t/15+14a/(30*2^31), whose last term stays below 1/15 for a<2^28, so bits 31 to 35 of P are t for t<15
    // and t+1 above. AVX-512 VBMI looks u up by them directly, a 64-entry permute whose index bit 5, bit 36 of P, is
    // b's parity; every other rung forms t=a-30b.
    //
    // Determinism: .NET binary64 arithmetic is IEEE 754 with round-to-nearest-even and is never contracted into a
    // fused multiply-add, so the estimate is the same on every host and width. The answer does not depend on it
    // beyond the bound above. Simulation state never holds the floating-point value.

    /// <summary>The binary64 reciprocal the reciprocal rungs require for a divisor: 1.0/divisor, correctly rounded.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static double GourdonReciprocal(uint divisor) => (1D / divisor);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void DivideGourdonSegmentPositions(ulong dividend, ReadOnlySpan<uint> divisors, ReadOnlySpan<double> reciprocals,
        uint low, Span<ulong> positions) {
        // Instruction-set dispatch folds when the caller is compiled; every rung writes the same positions.
        if (Vector512.IsHardwareAccelerated && Avx512Vbmi.IsSupported) {
            DivideGourdonSegmentPositions512Core<GourdonUnitsByFraction>(dividend: dividend, divisors: divisors, low: low, positions: positions,
                reciprocals: reciprocals);
        } else if (Vector512.IsHardwareAccelerated) {
            DivideGourdonSegmentPositions512Core<GourdonUnitsByHalves>(dividend: dividend, divisors: divisors, low: low, positions: positions,
                reciprocals: reciprocals);
        } else if (Vector256.IsHardwareAccelerated) {
            DivideGourdonSegmentPositions256Core(dividend: dividend, divisors: divisors, low: low, positions: positions, reciprocals: reciprocals);
        } else {
            DivideGourdonSegmentPositionsScalarCore(dividend: dividend, divisors: divisors, low: low, positions: positions, reciprocals: reciprocals, start: 0);
        }
    }

    /// <summary>Writes, for each leaf quotient <c>Q = floor(dividend / divisors[i])</c>, the packed position of the bit
    /// just above Q's in a thirty-wheel bitmap whose bit zero stands for <paramref name="low"/>, with 512-bit vectors.</summary>
    /// <typeparam name="TUnits">How a lane finds the units below its remainder; <see cref="GourdonUnitsByFraction"/>
    /// requires AVX-512 VBMI.</typeparam>
    /// <param name="dividend">The dividend shared by every quotient.</param>
    /// <param name="divisors">The divisors, each from 1 through 2^31 - 1.</param>
    /// <param name="reciprocals">Entry <c>i</c> is <c>1.0 / divisors[i]</c>, correctly rounded; at least as long as
    /// <paramref name="divisors"/>.</param>
    /// <param name="low">The integer bit zero stands for; at most every quotient, and every quotient below
    /// <c>low + 2^28 - 1</c>.</param>
    /// <param name="positions">Receives the positions; at least as long as <paramref name="divisors"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="reciprocals"/> or <paramref name="positions"/> is
    /// shorter than <paramref name="divisors"/>, or a divisor is zero or above 2^31 - 1, or its quotient lies below
    /// <paramref name="low"/> or at or above <c>low + 2^28 - 1</c>; the message names the divisor's index.</exception>
    /// <exception cref="ArgumentException">A reciprocal is not the correctly rounded <c>1.0 / divisor</c>; the message
    /// names its index.</exception>
    /// <exception cref="PlatformNotSupportedException"><typeparamref name="TUnits"/> requires an instruction set the host
    /// lacks.</exception>
    /// <remarks>
    /// <para>Position <c>i</c> packs <c>n</c>, the number of offsets from zero through <c>Q - low</c> coprime to thirty:
    /// its high 32 bits are the word <c>floor(n / 64)</c>, which holds Q's bit or begins just above it, and its low six
    /// bits are <c>n mod 64</c>, below which that word's bits stand for its units up to Q.</para>
    /// <para>The positions are exact when every divisor and quotient lies in the stated ranges and every reciprocal is
    /// the correctly rounded inverse of its divisor. The shell makes one pass over the run, with one hardware division
    /// per element, to check all three before it writes anything; the counting engine, which establishes them once
    /// per table, calls the internal core. The law <c>prime-selection.gourdon-quotient-rungs</c> compares every rung
    /// with exact division.</para>
    /// </remarks>
    public static void DivideGourdonSegmentPositions512<TUnits>(ulong dividend, ReadOnlySpan<uint> divisors, ReadOnlySpan<double> reciprocals,
        uint low, Span<ulong> positions)
        where TUnits : struct, IGourdonUnitsBelow512 {
        ArgumentOutOfRangeException.ThrowIfLessThan(value: reciprocals.Length, other: divisors.Length, paramName: nameof(reciprocals));
        ArgumentOutOfRangeException.ThrowIfLessThan(value: positions.Length, other: divisors.Length, paramName: nameof(positions));
        PrimeKernelGuard.RequireInstructionSet(instructionSet: TUnits.InstructionSet, supported: TUnits.IsSupported);
        RequireSegmentDivisors(dividend: dividend, divisors: divisors, low: low, reciprocals: reciprocals, start: 0);
        DivideGourdonSegmentPositions512Core<TUnits>(dividend: dividend, divisors: divisors, low: low, positions: positions, reciprocals: reciprocals);
    }

    // The error bound needs 0<d<2^31, a reciprocal equal to the correctly rounded 1.0/d, and 1<=a=Q-low+1<2^28 for the
    // quotient Q=floor(n/d); a is below 2^28 exactly when Q<low+2^28-1.
    private static void RequireSegmentDivisors(ulong dividend, ReadOnlySpan<uint> divisors, ReadOnlySpan<double> reciprocals, uint low, int start) {
        var ceiling = ((((ulong)low) + (1UL << 28)) - 1UL);

        for (var index = start; (index < divisors.Length); ++index) {
            var divisor = divisors[index];

            if ((divisor == 0U) || (divisor > int.MaxValue)) {
                throw new ArgumentOutOfRangeException(paramName: nameof(divisors), actualValue: divisor, message: $"divisors[{index}] is not from 1 through 2^31 - 1.");
            }
            if (BitConverter.DoubleToUInt64Bits(value: reciprocals[index]) != BitConverter.DoubleToUInt64Bits(value: GourdonReciprocal(divisor: divisor))) {
                throw new ArgumentException(message: $"reciprocals[{index}] is not the correctly rounded 1.0 / divisors[{index}].", paramName: nameof(reciprocals));
            }

            var quotient = (dividend / divisor);

            if ((quotient < low) || (quotient >= ceiling)) {
                throw new ArgumentOutOfRangeException(paramName: nameof(divisors), actualValue: divisor,
                    message: $"The quotient of divisors[{index}] lies outside the window from low through low + 2^28 - 2.");
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void DivideGourdonSegmentPositions512Core<TUnits>(ulong dividend, ReadOnlySpan<uint> divisors, ReadOnlySpan<double> reciprocals,
        uint low, Span<ulong> positions)
        where TUnits : struct, IGourdonUnitsBelow512 {
        Debug.Assert(((reciprocals.Length >= divisors.Length) && (positions.Length >= divisors.Length)));
        ref var divisor = ref MemoryMarshal.GetReference(span: divisors);
        ref var reciprocal = ref MemoryMarshal.GetReference(span: reciprocals);
        ref var position = ref MemoryMarshal.GetReference(span: positions);
        var numerator = Vector512.Create(value: dividend);
        var estimate = Vector512.Create(value: ((double)dividend));
        var biasValue = Vector512.Create(value: GourdonQuotientBiasValue);
        // An encoding less this is R-(low-1), which is a or a+1.
        var origin = Vector512.Create(value: ((GourdonQuotientBias + low) - 1UL));
        var byteReciprocal = Vector512.Create(value: GourdonByteReciprocal);
        var packing = Vector512.Create(value: GourdonPositionPacking);
        var last = (divisors.Length - Vector512<ulong>.Count);
        nint index = 0;

        for (; (index <= last); index += Vector512<ulong>.Count) {
            var wide = Vector512.WidenLower(source: Vector256.LoadUnsafe(elementOffset: ((nuint)index), source: ref divisor).ToVector512Unsafe());
            var encoding = ((estimate * Vector512.LoadUnsafe(elementOffset: ((nuint)index), source: ref reciprocal)) + biasValue).AsUInt64();
            var remainder = (numerator - MultiplyLow32(left: encoding, right: wide));
            // Only the low 32 bits of each lane of above are a; every step below reads no others.
            var above = ((encoding - origin).AsInt32() + Vector512.ShiftRightArithmetic(shiftCount: 31, vector: remainder.AsInt32())).AsUInt64();
            var product = MultiplyLow32(left: above, right: byteReciprocal);
            var bytes = Vector512.ShiftRightLogical(shiftCount: 36, vector: product);

            InsertLowBits(field: TUnits.Below(above: above, bytes: bytes, product: product), value: MultiplyLow32(left: bytes, right: packing))
                .StoreUnsafe(destination: ref position, elementOffset: ((nuint)index));
        }
        DivideGourdonSegmentPositionsScalarCore(dividend: dividend, divisors: divisors, low: low, positions: positions, reciprocals: reciprocals,
            start: ((int)index));
    }

    /// <summary>Writes each leaf quotient's packed bitmap position with 256-bit vectors.</summary>
    /// <param name="dividend">The dividend shared by every quotient.</param>
    /// <param name="divisors">The divisors, each from 1 through 2^31 - 1.</param>
    /// <param name="reciprocals">Entry <c>i</c> is <c>1.0 / divisors[i]</c>, correctly rounded.</param>
    /// <param name="low">The integer bit zero stands for.</param>
    /// <param name="positions">Receives the positions.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="reciprocals"/> or <paramref name="positions"/> is
    /// shorter than <paramref name="divisors"/>, or a divisor is zero or above 2^31 - 1, or its quotient lies below
    /// <paramref name="low"/> or at or above <c>low + 2^28 - 1</c>; the message names the divisor's index.</exception>
    /// <exception cref="ArgumentException">A reciprocal is not the correctly rounded <c>1.0 / divisor</c>; the message
    /// names its index.</exception>
    /// <remarks>The 256-bit rung of <see cref="DivideGourdonSegmentPositions512"/>, with the same contract; it needs no
    /// instruction set beyond the host's accelerated vectors.</remarks>
    public static void DivideGourdonSegmentPositions256(ulong dividend, ReadOnlySpan<uint> divisors, ReadOnlySpan<double> reciprocals,
        uint low, Span<ulong> positions) {
        ArgumentOutOfRangeException.ThrowIfLessThan(value: reciprocals.Length, other: divisors.Length, paramName: nameof(reciprocals));
        ArgumentOutOfRangeException.ThrowIfLessThan(value: positions.Length, other: divisors.Length, paramName: nameof(positions));
        RequireSegmentDivisors(dividend: dividend, divisors: divisors, low: low, reciprocals: reciprocals, start: 0);
        DivideGourdonSegmentPositions256Core(dividend: dividend, divisors: divisors, low: low, positions: positions, reciprocals: reciprocals);
    }

    // AVX2 has no 64-bit multiply, and the remainder's low 32 bits need none; their sign spreads over each lane, so a
    // stays whole and t=a-30b exact.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void DivideGourdonSegmentPositions256Core(ulong dividend, ReadOnlySpan<uint> divisors, ReadOnlySpan<double> reciprocals,
        uint low, Span<ulong> positions) {
        Debug.Assert(((reciprocals.Length >= divisors.Length) && (positions.Length >= divisors.Length)));
        ref var divisor = ref MemoryMarshal.GetReference(span: divisors);
        ref var reciprocal = ref MemoryMarshal.GetReference(span: reciprocals);
        ref var position = ref MemoryMarshal.GetReference(span: positions);
        var numerator = Vector256.Create(value: dividend);
        var estimate = Vector256.Create(value: ((double)dividend));
        var biasValue = Vector256.Create(value: GourdonQuotientBiasValue);
        var origin = Vector256.Create(value: ((GourdonQuotientBias + low) - 1UL));
        var byteReciprocal = Vector256.Create(value: GourdonByteReciprocal);
        var byteWidth = Vector256.Create(value: ((ulong)PrimeWheel30.Modulus));
        var unitsBelow = Vector256.Create(value: Vector128.Create(values: PrimeWheel30.UnitsBelowHalves));
        var packing = Vector256.Create(value: GourdonPositionPacking);
        var last = (divisors.Length - Vector256<ulong>.Count);
        nint index = 0;

        for (; (index <= last); index += Vector256<ulong>.Count) {
            var wide = Vector256.WidenLower(source: Vector128.LoadUnsafe(elementOffset: ((nuint)index), source: ref divisor).ToVector256Unsafe());
            var encoding = ((estimate * Vector256.LoadUnsafe(elementOffset: ((nuint)index), source: ref reciprocal)) + biasValue).AsUInt64();
            var above = ((encoding - origin) + LowSignMask(value: (numerator - MultiplyLow32(left: encoding, right: wide))));
            var bytes = Vector256.ShiftRightLogical(shiftCount: 36, vector: MultiplyLow32(left: above, right: byteReciprocal));
            var halves = Vector256.ShiftRightLogical(shiftCount: 1, vector: (above - MultiplyLow32(left: bytes, right: byteWidth)));

            (MultiplyLow32(left: bytes, right: packing) + ShuffleBytes(indices: halves, table: unitsBelow))
                .StoreUnsafe(destination: ref position, elementOffset: ((nuint)index));
        }
        DivideGourdonSegmentPositionsScalarCore(dividend: dividend, divisors: divisors, low: low, positions: positions, reciprocals: reciprocals,
            start: ((int)index));
    }

    /// <summary>Writes each leaf quotient's packed bitmap position from one index on, one at a time.</summary>
    /// <param name="dividend">The dividend shared by every quotient.</param>
    /// <param name="divisors">The divisors, each from 1 through 2^31 - 1.</param>
    /// <param name="reciprocals">Entry <c>i</c> is <c>1.0 / divisors[i]</c>, correctly rounded.</param>
    /// <param name="low">The integer bit zero stands for.</param>
    /// <param name="positions">Receives the positions.</param>
    /// <param name="start">The first index written, from zero through the number of divisors.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="reciprocals"/> or <paramref name="positions"/> is
    /// shorter than <paramref name="divisors"/>, <paramref name="start"/> is negative or past the end of
    /// <paramref name="divisors"/>, or a divisor from <paramref name="start"/> on is zero or above 2^31 - 1, or its
    /// quotient lies below <paramref name="low"/> or at or above <c>low + 2^28 - 1</c>; the message names the divisor's
    /// index.</exception>
    /// <exception cref="ArgumentException">A reciprocal from <paramref name="start"/> on is not the correctly rounded
    /// <c>1.0 / divisor</c>; the message names its index.</exception>
    /// <remarks>The scalar rung of <see cref="DivideGourdonSegmentPositions512"/>, with the same contract.</remarks>
    public static void DivideGourdonSegmentPositionsScalar(ulong dividend, ReadOnlySpan<uint> divisors, ReadOnlySpan<double> reciprocals,
        uint low, Span<ulong> positions, int start) {
        ArgumentOutOfRangeException.ThrowIfLessThan(value: reciprocals.Length, other: divisors.Length, paramName: nameof(reciprocals));
        ArgumentOutOfRangeException.ThrowIfLessThan(value: positions.Length, other: divisors.Length, paramName: nameof(positions));
        ArgumentOutOfRangeException.ThrowIfNegative(value: start, paramName: nameof(start));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: start, other: divisors.Length, paramName: nameof(start));
        RequireSegmentDivisors(dividend: dividend, divisors: divisors, low: low, reciprocals: reciprocals, start: start);
        DivideGourdonSegmentPositionsScalarCore(dividend: dividend, divisors: divisors, low: low, positions: positions, reciprocals: reciprocals,
            start: start);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void DivideGourdonSegmentPositionsScalarCore(ulong dividend, ReadOnlySpan<uint> divisors, ReadOnlySpan<double> reciprocals,
        uint low, Span<ulong> positions, int start) {
        Debug.Assert(((reciprocals.Length >= divisors.Length) && (positions.Length >= divisors.Length)));
        var estimate = ((double)dividend);
        var unitsBelow = PrimeWheel30.UnitsBelowHalves;

        for (var index = start; (index < divisors.Length); ++index) {
            var divisor = divisors[index];
            var rounded = (BitConverter.DoubleToUInt64Bits(value: ((estimate * reciprocals[index]) + GourdonQuotientBiasValue)) - GourdonQuotientBias);
            var remainder = ((long)(dividend - (rounded * divisor)));
            var above = ((uint)(((rounded - low) + 1UL) - ((remainder < 0) ? 1UL : 0UL)));

            var (bytes, within) = Math.DivRem(left: above, right: ((uint)PrimeWheel30.Modulus));

            positions[index] = ((bytes * GourdonPositionPacking) + unitsBelow[((int)(within >> 1))]);
        }
    }

    private const ulong GourdonByteReciprocal = 0x8888_8889;
    // 2^29+8: one multiply of a byte index b=8w+s places w in the high half of a position and 8s in its bit field.
    private const ulong GourdonPositionPacking = 0x2000_0008;

    // Finds u for the 512-bit segment-position rung: the units below t=a%30, in each lane's low three bits, the lane's
    // other bits arbitrary. Each lane's low 32 bits of above are a, product is a*0x88888889 and bytes is floor(a/30).
    /// <summary>Finds, for <see cref="DivideGourdonSegmentPositions512"/>, the units of thirty below each lane's
    /// remainder.</summary>
    public interface IGourdonUnitsBelow512 {
        /// <summary>Gets whether the host supports every instruction <see cref="Below"/> executes.</summary>
        static abstract bool IsSupported { get; }
        /// <summary>Gets the name of the instruction set <see cref="Below"/> requires, as the
        /// <see cref="PlatformNotSupportedException"/> of an unsupported host states it.</summary>
        static abstract string InstructionSet { get; }

        /// <summary>Returns, in each lane's low three bits, the units of thirty below <c>t = a mod 30</c>; the lane's other
        /// bits are arbitrary.</summary>
        /// <param name="above">Each lane's low 32 bits are <c>a</c>.</param>
        /// <param name="bytes">Each lane is <c>floor(a / 30)</c>.</param>
        /// <param name="product">Each lane is <c>a · 0x88888889</c>.</param>
        /// <returns>The units below each lane's remainder, in its low three bits.</returns>
        static abstract Vector512<ulong> Below(Vector512<ulong> above, Vector512<ulong> bytes, Vector512<ulong> product);
    }
    // Every host: t=a-30b, then PrimeWheel30.UnitsBelowHalves at t>>1 by a byte shuffle.
    /// <summary>Finds the units below a remainder by a byte shuffle over <see cref="PrimeWheel30.UnitsBelowHalves"/>, on
    /// every host.</summary>
    public readonly struct GourdonUnitsByHalves : IGourdonUnitsBelow512 {
        /// <inheritdoc/>
        public static bool IsSupported => true;
        /// <inheritdoc/>
        public static string InstructionSet => "no instruction set beyond vector support";

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector512<ulong> Below(Vector512<ulong> above, Vector512<ulong> bytes, Vector512<ulong> product) {
            var halves = Vector512.ShiftRightLogical(shiftCount: 1,
                vector: (above - MultiplyLow32(left: bytes, right: Vector512.Create(value: ((ulong)PrimeWheel30.Modulus)))));

            return ShuffleBytes(indices: halves, table: Vector512.Create(value: Vector128.Create(values: PrimeWheel30.UnitsBelowHalves)));
        }
    }
    // AVX-512 VBMI: bits 31 to 36 of P index GourdonUnitsByFractionTable, the position note's fraction lookup.
    /// <summary>Finds the units below a remainder by an AVX-512 VBMI permute over
    /// <see cref="GourdonUnitsByFractionTable"/>; use it only where AVX-512 VBMI is supported.</summary>
    public readonly struct GourdonUnitsByFraction : IGourdonUnitsBelow512 {
        /// <inheritdoc/>
        public static bool IsSupported => Avx512Vbmi.IsSupported;
        /// <inheritdoc/>
        public static string InstructionSet => "AVX-512 VBMI";

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector512<ulong> Below(Vector512<ulong> above, Vector512<ulong> bytes, Vector512<ulong> product) =>
            Avx512Vbmi.PermuteVar64x8(control: Vector512.ShiftRightLogical(shiftCount: 31, vector: product).AsByte(),
                left: Vector512.Create(values: GourdonUnitsByFractionTable)).AsUInt64();
    }

    /// <summary>Gets the units below each remainder, indexed by the remainder's fraction bits.</summary>
    /// <remarks>Entry <c>j + 32k</c>, for <c>j</c> below 32 and <c>k</c> below 2, is
    /// <c>PrimeWheel30.UnitsBelowHalves[t &gt;&gt; 1]</c> for <c>t = j - (j &gt;&gt; 4)</c>: the units below the remainder
    /// whose fraction bits are <c>j</c>. Entries 15 and 31 stand for no remainder. The values are written out as constant
    /// data; the law <c>prime-exploration.wheel-tables-match-their-derivation</c> proves they equal the derivation.</remarks>
    public static ReadOnlySpan<byte> GourdonUnitsByFractionTable => [
        0, 0, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 4, 4, 4, 4, 4, 5, 5, 6, 6, 6, 6, 7, 7, 7, 7, 7, 7, 8,
        0, 0, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 4, 4, 4, 4, 4, 5, 5, 6, 6, 6, 6, 7, 7, 7, 7, 7, 7, 8,
    ];

    // Replaces the low three bits of each lane of value, which must be zero, with those of field.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector512<ulong> InsertLowBits(Vector512<ulong> value, Vector512<ulong> field) =>
        (Avx512F.IsSupported ? Avx512F.TernaryLogic(a: value, b: field, c: Vector512.Create(value: 7UL), control: 0xF8)
            : value | (field & Vector512.Create(value: 7UL)));
    // All ones in each lane whose bit 31 is set, zero in the others.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<ulong> LowSignMask(Vector256<ulong> value) =>
        (Avx2.IsSupported ? Avx2.Shuffle(control: 0b10_10_00_00, value: Avx2.ShiftRightArithmetic(count: 31, value: value.AsInt32())).AsUInt64()
            : Vector256.ShiftRightArithmetic(shiftCount: 63, vector: Vector256.ShiftLeft(shiftCount: 32, vector: value.AsInt64())).AsUInt64());
    // The low byte of each lane is table[i] for the low byte i of that lane of indices, which must be below 16, where
    // table repeats one sixteen-byte table in every 128-bit lane: the in-lane byte shuffle and the whole-vector one then
    // agree on it. The other bytes are zero where indices' are, and arbitrary otherwise.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector512<ulong> ShuffleBytes(Vector512<byte> table, Vector512<ulong> indices) =>
        (Avx512BW.IsSupported ? Avx512BW.Shuffle(mask: indices.AsByte(), value: table) : Vector512.Shuffle(indices: indices.AsByte(), vector: table)).AsUInt64();
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<ulong> ShuffleBytes(Vector256<byte> table, Vector256<ulong> indices) =>
        (Avx2.IsSupported ? Avx2.Shuffle(mask: indices.AsByte(), value: table) : Vector256.Shuffle(indices: indices.AsByte(), vector: table)).AsUInt64();
    // The 64-bit products of the low 32 bits of each lane's factors.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector512<ulong> MultiplyLow32(Vector512<ulong> left, Vector512<ulong> right) =>
        (Avx512F.IsSupported ? Avx512F.Multiply(left: left.AsUInt32(), right: right.AsUInt32())
            : ((left & Vector512.Create(value: ((ulong)uint.MaxValue))) * (right & Vector512.Create(value: ((ulong)uint.MaxValue)))));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<ulong> MultiplyLow32(Vector256<ulong> left, Vector256<ulong> right) =>
        (Avx2.IsSupported ? Avx2.Multiply(left: left.AsUInt32(), right: right.AsUInt32())
            : ((left & Vector256.Create(value: ((ulong)uint.MaxValue))) * (right & Vector256.Create(value: ((ulong)uint.MaxValue)))));
    // The low 64 bits of each lane's product, where every lane of right is below 2^32. AVX2 and SSE2 multiply only
    // 32-bit halves: left*right = low(left)*right + (high(left)*right mod 2^32)*2^32 modulo 2^64, two multiplies where
    // a general 64-bit product takes three.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<ulong> MultiplyByUInt32(Vector256<ulong> left, Vector256<ulong> right) =>
        (Avx2.IsSupported ? (Avx2.Multiply(left: left.AsUInt32(), right: right.AsUInt32())
            + Vector256.ShiftLeft(vector: Avx2.Multiply(left: Vector256.ShiftRightLogical(shiftCount: 32, vector: left).AsUInt32(), right: right.AsUInt32()), shiftCount: 32))
            : (left * right));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ulong> MultiplyByUInt32(Vector128<ulong> left, Vector128<ulong> right) =>
        (Sse2.IsSupported ? (Sse2.Multiply(left: left.AsUInt32(), right: right.AsUInt32())
            + Vector128.ShiftLeft(vector: Sse2.Multiply(left: Vector128.ShiftRightLogical(shiftCount: 32, vector: left).AsUInt32(), right: right.AsUInt32()), shiftCount: 32))
            : (left * right));
}
