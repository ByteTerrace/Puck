using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Puck.Maths;

public static partial class PrimeExtensions {
    // Leaf quotients floor(n/d) for one dividend n and a run of divisors d. Every rung computes the same exact
    // integers; floating point only proposes an estimate that one integer step corrects.
    //
    // Error bound. Let n<2^64, 0<d<2^32 and Q=floor(n/d)<2^48. Converting n to binary64 errs by at most one
    // unit in the last place, a relative error of at most 2^-52; d converts exactly; IEEE division rounds once
    // more by at most 2^-53. The estimate e therefore satisfies |e-n/d| < (n/d)*2^-51.4 < 2^48*2^-51.4 < 1/8.
    // Adding 2^52 lands in [2^52,2^53), where binary64 integers are spaced by one, so round-to-nearest yields an
    // integer R with |R-e| <= 1/2, and the low 52 bits of the sum's encoding are R itself. Hence |R-n/d| < 5/8
    // and R is Q or Q+1. The remainder n-R*d, computed modulo 2^64, lies in [-d,d) and so is exact as a signed 64-bit
    // value; it is negative exactly when R=Q+1, and subtracting that one unit yields Q.
    //
    // Determinism: .NET binary64 arithmetic is IEEE 754 with round-to-nearest-even, so the estimate is the same
    // on every host and width. The answer does not depend on it beyond the bound above: any estimate within one
    // half of n/d gives the same Q. Simulation state never holds the floating-point value.
    internal const int GourdonQuotientBatchSize = 64;

    private const ulong GourdonQuotientBias = 0x4330_0000_0000_0000;
    private const double GourdonQuotientBiasValue = 4_503_599_627_370_496D;

    /// <summary>The exclusive bound on every quotient the leaf-quotient rungs accept: 2^48.</summary>
    public const ulong GourdonQuotientLimit = (1UL << 48);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void DivideGourdonQuotients(ulong dividend, ReadOnlySpan<uint> divisors, Span<ulong> quotients) {
        // Instruction-set dispatch folds when the caller is compiled; every rung returns the same quotients.
        if (Vector512.IsHardwareAccelerated) {
            DivideGourdonQuotients512(dividend: dividend, divisors: divisors, quotients: quotients);
        } else if (Vector256.IsHardwareAccelerated) {
            DivideGourdonQuotients256(dividend: dividend, divisors: divisors, quotients: quotients);
        } else if (Vector128.IsHardwareAccelerated) {
            DivideGourdonQuotients128(dividend: dividend, divisors: divisors, quotients: quotients);
        } else {
            DivideGourdonQuotientsScalar(dividend: dividend, divisors: divisors, quotients: quotients, start: 0);
        }
    }

    // Writes quotients[i]=floor(dividend/divisors[i]). Requires quotients.Length>=divisors.Length, every divisor
    // positive, and every quotient below GourdonQuotientLimit.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void DivideGourdonQuotients512(ulong dividend, ReadOnlySpan<uint> divisors, Span<ulong> quotients) {
        Debug.Assert((quotients.Length >= divisors.Length));
        ref var divisor = ref MemoryMarshal.GetReference(span: divisors);
        ref var quotient = ref MemoryMarshal.GetReference(span: quotients);
        var numerator = Vector512.Create(value: dividend);
        var estimate = Vector512.Create(value: ((double)dividend));
        var bias = Vector512.Create(value: GourdonQuotientBias);
        var biasValue = Vector512.Create(value: GourdonQuotientBiasValue);
        var index = 0;

        for (; (index <= (divisors.Length - Vector512<ulong>.Count)); index += Vector512<ulong>.Count) {
            var wide = Vector512.WidenLower(source: Vector256.LoadUnsafe(elementOffset: ((nuint)index), source: ref divisor).ToVector512Unsafe());
            var rounded = (((estimate / ((wide | bias).AsDouble() - biasValue)) + biasValue).AsUInt64() - bias);
            var remainder = (numerator - (rounded * wide)).AsInt64();

            (rounded + Vector512.LessThan(left: remainder, right: Vector512<long>.Zero).AsUInt64())
                .StoreUnsafe(destination: ref quotient, elementOffset: ((nuint)index));
        }
        DivideGourdonQuotientsScalar(dividend: dividend, divisors: divisors, quotients: quotients, start: index);
    }
    // The 256-bit rung of DivideGourdonQuotients512, same contract.
    public static void DivideGourdonQuotients256(ulong dividend, ReadOnlySpan<uint> divisors, Span<ulong> quotients) {
        Debug.Assert((quotients.Length >= divisors.Length));
        ref var divisor = ref MemoryMarshal.GetReference(span: divisors);
        ref var quotient = ref MemoryMarshal.GetReference(span: quotients);
        var numerator = Vector256.Create(value: dividend);
        var estimate = Vector256.Create(value: ((double)dividend));
        var bias = Vector256.Create(value: GourdonQuotientBias);
        var biasValue = Vector256.Create(value: GourdonQuotientBiasValue);
        var index = 0;

        for (; (index <= (divisors.Length - Vector256<ulong>.Count)); index += Vector256<ulong>.Count) {
            var wide = Vector256.WidenLower(source: Vector128.LoadUnsafe(elementOffset: ((nuint)index), source: ref divisor).ToVector256Unsafe());
            var rounded = (((estimate / ((wide | bias).AsDouble() - biasValue)) + biasValue).AsUInt64() - bias);
            var remainder = (numerator - (rounded * wide)).AsInt64();

            (rounded + Vector256.LessThan(left: remainder, right: Vector256<long>.Zero).AsUInt64())
                .StoreUnsafe(destination: ref quotient, elementOffset: ((nuint)index));
        }
        DivideGourdonQuotientsScalar(dividend: dividend, divisors: divisors, quotients: quotients, start: index);
    }
    // The 128-bit rung of DivideGourdonQuotients512, same contract.
    public static void DivideGourdonQuotients128(ulong dividend, ReadOnlySpan<uint> divisors, Span<ulong> quotients) {
        Debug.Assert((quotients.Length >= divisors.Length));
        ref var divisor = ref MemoryMarshal.GetReference(span: divisors);
        ref var quotient = ref MemoryMarshal.GetReference(span: quotients);
        var numerator = Vector128.Create(value: dividend);
        var estimate = Vector128.Create(value: ((double)dividend));
        var bias = Vector128.Create(value: GourdonQuotientBias);
        var biasValue = Vector128.Create(value: GourdonQuotientBiasValue);
        var index = 0;

        for (; (index <= (divisors.Length - Vector128<ulong>.Count)); index += Vector128<ulong>.Count) {
            var pair = Unsafe.ReadUnaligned<ulong>(source: ref Unsafe.As<uint, byte>(source: ref Unsafe.Add(elementOffset: index, source: ref divisor)));
            var wide = Vector128.WidenLower(source: Vector128.CreateScalarUnsafe(value: pair).AsUInt32());
            var rounded = (((estimate / ((wide | bias).AsDouble() - biasValue)) + biasValue).AsUInt64() - bias);
            var remainder = (numerator - (rounded * wide)).AsInt64();

            (rounded + Vector128.LessThan(left: remainder, right: Vector128<long>.Zero).AsUInt64())
                .StoreUnsafe(destination: ref quotient, elementOffset: ((nuint)index));
        }
        DivideGourdonQuotientsScalar(dividend: dividend, divisors: divisors, quotients: quotients, start: index);
    }
    // The scalar rung, and every vector rung's tail from start, same contract.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void DivideGourdonQuotientsScalar(ulong dividend, ReadOnlySpan<uint> divisors, Span<ulong> quotients, int start) {
        Debug.Assert((quotients.Length >= divisors.Length));
        var estimate = ((double)dividend);

        for (var index = start; (index < divisors.Length); ++index) {
            var divisor = divisors[index];
            var rounded = (BitConverter.DoubleToUInt64Bits(value: ((estimate / divisor) + GourdonQuotientBiasValue)) - GourdonQuotientBias);
            var remainder = ((long)(dividend - (rounded * divisor)));

            quotients[index] = (rounded - ((remainder < 0) ? 1UL : 0UL));
        }
    }
}
