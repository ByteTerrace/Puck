using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Puck.Maths;

/// <summary>The division kernels of the combinatorial prime counter: the leaf quotients of a run of divisors, their
/// packed positions in a thirty-wheel bitmap, and division by a fixed divisor through its floor reciprocal.</summary>
/// <remarks>
/// <para>Each public method is a checked shell over an internal core with the same body. The shell validates every
/// precondition before it writes anything: span lengths, start indices and the host's instruction set in constant time,
/// and the divisors, reciprocals and quotient ranges in one pass over the run. The counting engine's loops call the core
/// directly, having established the same facts once for the whole table.</para>
/// <para>Every rung computes the same integers, so a law compares each rung with exact division on identical inputs.</para>
/// </remarks>
public static partial class PrimeDivisionKernels {
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

    internal const double GourdonQuotientBiasValue = 4_503_599_627_370_496D;

    /// <summary>The exclusive bound on every quotient the leaf-quotient rungs accept: 2^48.</summary>
    public const ulong GourdonQuotientLimit = (1UL << 48);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void DivideGourdonQuotients(ulong dividend, ReadOnlySpan<uint> divisors, Span<ulong> quotients) {
        // Instruction-set dispatch folds when the caller is compiled; every rung returns the same quotients.
        if (Vector512.IsHardwareAccelerated) {
            DivideGourdonQuotients512Core(dividend: dividend, divisors: divisors, quotients: quotients);
        } else if (Vector256.IsHardwareAccelerated) {
            DivideGourdonQuotients256Core(dividend: dividend, divisors: divisors, quotients: quotients);
        } else if (Vector128.IsHardwareAccelerated) {
            DivideGourdonQuotients128Core(dividend: dividend, divisors: divisors, quotients: quotients);
        } else {
            DivideGourdonQuotientsScalarCore(dividend: dividend, divisors: divisors, quotients: quotients, start: 0);
        }
    }

    /// <summary>Writes each leaf quotient <c>floor(dividend / divisors[i])</c> with 512-bit vectors.</summary>
    /// <param name="dividend">The dividend shared by every quotient.</param>
    /// <param name="divisors">The divisors, each positive.</param>
    /// <param name="quotients">Receives the quotients; at least as long as <paramref name="divisors"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="quotients"/> is shorter than <paramref name="divisors"/>,
    /// or a divisor is zero or gives a quotient of <see cref="GourdonQuotientLimit"/> or more; the message names its index.</exception>
    /// <remarks>
    /// <para>Every quotient is exact while it stays below <see cref="GourdonQuotientLimit"/> and its divisor is
    /// positive. The shell makes one pass over the divisors to check both before it writes anything; the counting
    /// engine, which establishes them once per table, calls the internal core. The rung needs no instruction set
    /// beyond the host's accelerated vectors: a host without 512-bit vectors runs it in software.</para>
    /// <para>A run of at least one vector ends with a vector that overlaps the previous one, rewriting those quotients
    /// with the same values; a shorter run divides one by one. The law <c>prime-selection.gourdon-quotient-rungs</c>
    /// compares every rung with exact division.</para>
    /// </remarks>
    public static void DivideGourdonQuotients512(ulong dividend, ReadOnlySpan<uint> divisors, Span<ulong> quotients) {
        ArgumentOutOfRangeException.ThrowIfLessThan(value: quotients.Length, other: divisors.Length, paramName: nameof(quotients));
        RequireQuotientDivisors(dividend: dividend, divisors: divisors, start: 0);
        DivideGourdonQuotients512Core(dividend: dividend, divisors: divisors, quotients: quotients);
    }
    /// <summary>Writes each leaf quotient <c>floor(dividend / divisors[i])</c> with 256-bit vectors.</summary>
    /// <param name="dividend">The dividend shared by every quotient.</param>
    /// <param name="divisors">The divisors, each positive.</param>
    /// <param name="quotients">Receives the quotients; at least as long as <paramref name="divisors"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="quotients"/> is shorter than <paramref name="divisors"/>,
    /// or a divisor is zero or gives a quotient of <see cref="GourdonQuotientLimit"/> or more; the message names its index.</exception>
    /// <remarks>The 256-bit rung of <see cref="DivideGourdonQuotients512"/>, with the same contract.</remarks>
    public static void DivideGourdonQuotients256(ulong dividend, ReadOnlySpan<uint> divisors, Span<ulong> quotients) {
        ArgumentOutOfRangeException.ThrowIfLessThan(value: quotients.Length, other: divisors.Length, paramName: nameof(quotients));
        RequireQuotientDivisors(dividend: dividend, divisors: divisors, start: 0);
        DivideGourdonQuotients256Core(dividend: dividend, divisors: divisors, quotients: quotients);
    }
    /// <summary>Writes each leaf quotient <c>floor(dividend / divisors[i])</c> with 128-bit vectors.</summary>
    /// <param name="dividend">The dividend shared by every quotient.</param>
    /// <param name="divisors">The divisors, each positive.</param>
    /// <param name="quotients">Receives the quotients; at least as long as <paramref name="divisors"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="quotients"/> is shorter than <paramref name="divisors"/>,
    /// or a divisor is zero or gives a quotient of <see cref="GourdonQuotientLimit"/> or more; the message names its index.</exception>
    /// <remarks>The 128-bit rung of <see cref="DivideGourdonQuotients512"/>, with the same contract.</remarks>
    public static void DivideGourdonQuotients128(ulong dividend, ReadOnlySpan<uint> divisors, Span<ulong> quotients) {
        ArgumentOutOfRangeException.ThrowIfLessThan(value: quotients.Length, other: divisors.Length, paramName: nameof(quotients));
        RequireQuotientDivisors(dividend: dividend, divisors: divisors, start: 0);
        DivideGourdonQuotients128Core(dividend: dividend, divisors: divisors, quotients: quotients);
    }
    /// <summary>Writes each leaf quotient <c>floor(dividend / divisors[i])</c> from one index on, one at a time.</summary>
    /// <param name="dividend">The dividend shared by every quotient.</param>
    /// <param name="divisors">The divisors, each positive.</param>
    /// <param name="quotients">Receives the quotients; at least as long as <paramref name="divisors"/>.</param>
    /// <param name="start">The first index written, from zero through the number of divisors.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="quotients"/> is shorter than <paramref name="divisors"/>,
    /// <paramref name="start"/> is negative or past the end of <paramref name="divisors"/>, or a divisor from
    /// <paramref name="start"/> on is zero or gives a quotient of <see cref="GourdonQuotientLimit"/> or more; the message
    /// names its index.</exception>
    /// <remarks>The scalar rung of <see cref="DivideGourdonQuotients512"/>, with the same contract.</remarks>
    public static void DivideGourdonQuotientsScalar(ulong dividend, ReadOnlySpan<uint> divisors, Span<ulong> quotients, int start) {
        ArgumentOutOfRangeException.ThrowIfLessThan(value: quotients.Length, other: divisors.Length, paramName: nameof(quotients));
        ArgumentOutOfRangeException.ThrowIfNegative(value: start, paramName: nameof(start));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: start, other: divisors.Length, paramName: nameof(start));
        RequireQuotientDivisors(dividend: dividend, divisors: divisors, start: start);
        DivideGourdonQuotientsScalarCore(dividend: dividend, divisors: divisors, quotients: quotients, start: start);
    }

    // A quotient floor(n/d) reaches 2^48 exactly when d <= n >> 48, so one comparison per divisor covers the limit.
    private static void RequireQuotientDivisors(ulong dividend, ReadOnlySpan<uint> divisors, int start) {
        var reaching = (dividend >>> 48);

        for (var index = start; (index < divisors.Length); ++index) {
            var divisor = divisors[index];

            if (divisor == 0U) {
                throw new ArgumentOutOfRangeException(paramName: nameof(divisors), actualValue: divisor, message: $"divisors[{index}] is zero.");
            }
            if (divisor <= reaching) {
                throw new ArgumentOutOfRangeException(paramName: nameof(divisors), actualValue: divisor,
                    message: $"divisors[{index}] gives a quotient of GourdonQuotientLimit or more.");
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void DivideGourdonQuotients512Core(ulong dividend, ReadOnlySpan<uint> divisors, Span<ulong> quotients) {
        var length = divisors.Length;

        Debug.Assert((quotients.Length >= length));
        if (length < Vector512<ulong>.Count) {
            DivideGourdonQuotientsScalarCore(dividend: dividend, divisors: divisors, quotients: quotients, start: 0);
            return;
        }
        ref var divisor = ref MemoryMarshal.GetReference(span: divisors);
        ref var quotient = ref MemoryMarshal.GetReference(span: quotients);
        var numerator = Vector512.Create(value: dividend);
        var estimate = Vector512.Create(value: ((double)dividend));
        var bias = Vector512.Create(value: GourdonQuotientBias);
        var biasValue = Vector512.Create(value: GourdonQuotientBiasValue);
        var final = ((nuint)(length - Vector512<ulong>.Count));

        for (var index = ((nuint)0); ; index = nuint.Min(x: (index + ((nuint)Vector512<ulong>.Count)), y: final)) {
            var wide = Vector512.WidenLower(source: Vector256.LoadUnsafe(elementOffset: index, source: ref divisor).ToVector512Unsafe());
            var rounded = (((estimate / ((wide | bias).AsDouble() - biasValue)) + biasValue).AsUInt64() - bias);
            var remainder = (numerator - (rounded * wide)).AsInt64();

            (rounded + Vector512.LessThan(left: remainder, right: Vector512<long>.Zero).AsUInt64())
                .StoreUnsafe(destination: ref quotient, elementOffset: index);
            if (index == final) { break; }
        }
    }
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void DivideGourdonQuotients256Core(ulong dividend, ReadOnlySpan<uint> divisors, Span<ulong> quotients) {
        var length = divisors.Length;

        Debug.Assert((quotients.Length >= length));
        if (length < Vector256<ulong>.Count) {
            DivideGourdonQuotientsScalarCore(dividend: dividend, divisors: divisors, quotients: quotients, start: 0);
            return;
        }
        ref var divisor = ref MemoryMarshal.GetReference(span: divisors);
        ref var quotient = ref MemoryMarshal.GetReference(span: quotients);
        var numerator = Vector256.Create(value: dividend);
        var estimate = Vector256.Create(value: ((double)dividend));
        var bias = Vector256.Create(value: GourdonQuotientBias);
        var biasValue = Vector256.Create(value: GourdonQuotientBiasValue);
        var final = ((nuint)(length - Vector256<ulong>.Count));

        for (var index = ((nuint)0); ; index = nuint.Min(x: (index + ((nuint)Vector256<ulong>.Count)), y: final)) {
            var wide = Vector256.WidenLower(source: Vector128.LoadUnsafe(elementOffset: index, source: ref divisor).ToVector256Unsafe());
            var rounded = (((estimate / ((wide | bias).AsDouble() - biasValue)) + biasValue).AsUInt64() - bias);
            var remainder = (numerator - MultiplyByUInt32(left: rounded, right: wide)).AsInt64();

            (rounded + Vector256.LessThan(left: remainder, right: Vector256<long>.Zero).AsUInt64())
                .StoreUnsafe(destination: ref quotient, elementOffset: index);
            if (index == final) { break; }
        }
    }
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void DivideGourdonQuotients128Core(ulong dividend, ReadOnlySpan<uint> divisors, Span<ulong> quotients) {
        var length = divisors.Length;

        Debug.Assert((quotients.Length >= length));
        if (length < Vector128<ulong>.Count) {
            DivideGourdonQuotientsScalarCore(dividend: dividend, divisors: divisors, quotients: quotients, start: 0);
            return;
        }
        ref var divisor = ref MemoryMarshal.GetReference(span: divisors);
        ref var quotient = ref MemoryMarshal.GetReference(span: quotients);
        var numerator = Vector128.Create(value: dividend);
        var estimate = Vector128.Create(value: ((double)dividend));
        var bias = Vector128.Create(value: GourdonQuotientBias);
        var biasValue = Vector128.Create(value: GourdonQuotientBiasValue);
        var final = ((nuint)(length - Vector128<ulong>.Count));

        for (var index = ((nuint)0); ; index = nuint.Min(x: (index + ((nuint)Vector128<ulong>.Count)), y: final)) {
            var pair = Unsafe.ReadUnaligned<ulong>(source: ref Unsafe.As<uint, byte>(source: ref Unsafe.Add(elementOffset: index, source: ref divisor)));
            var wide = Vector128.WidenLower(source: Vector128.CreateScalarUnsafe(value: pair).AsUInt32());
            var rounded = (((estimate / ((wide | bias).AsDouble() - biasValue)) + biasValue).AsUInt64() - bias);
            var remainder = (numerator - MultiplyByUInt32(left: rounded, right: wide)).AsInt64();

            (rounded + Vector128.LessThan(left: remainder, right: Vector128<long>.Zero).AsUInt64())
                .StoreUnsafe(destination: ref quotient, elementOffset: index);
            if (index == final) { break; }
        }
    }
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void DivideGourdonQuotientsScalarCore(ulong dividend, ReadOnlySpan<uint> divisors, Span<ulong> quotients, int start) {
        var length = divisors.Length;

        Debug.Assert(((quotients.Length >= length) && (start >= 0)));
        ref var divisor = ref MemoryMarshal.GetReference(span: divisors);
        ref var quotient = ref MemoryMarshal.GetReference(span: quotients);
        var estimate = ((double)dividend);

        for (var index = start; (index < length); ++index) {
            var value = Unsafe.Add(elementOffset: index, source: ref divisor);
            var rounded = (BitConverter.DoubleToUInt64Bits(value: ((estimate / value) + GourdonQuotientBiasValue)) - GourdonQuotientBias);
            var remainder = ((long)(dividend - (rounded * value)));

            Unsafe.Add(elementOffset: index, source: ref quotient) = (rounded - ((remainder < 0) ? 1UL : 0UL));
        }
    }
}
