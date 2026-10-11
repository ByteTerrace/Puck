using System.Runtime.CompilerServices;

namespace Puck.Maths;

/// <summary>The kernels of the combinatorial prime counter's leaf stage: the hard-leaf survivor filters, the
/// thirty-wheel leaf sieve with its prefix cursor, and the wheel tables they read.</summary>
/// <remarks>
/// <para>Each public method is a checked shell over an internal core with the same body. The shell validates every
/// precondition that is constant-time to check, namely span lengths, index and coordinate bounds, alignment and the
/// host's instruction set, and throws before it writes anything; the counting engine's loops call the core directly,
/// having established the same facts once for the whole loop. A precondition on the data itself, such as the order of a
/// prime list, would cost a pass over it and stays the caller's, each such rule stated beside the member.</para>
/// <para>Every kernel is public so a law can drive it one step at a time and compare it with a direct oracle: the
/// aggregate <see cref="PrimeExtensions.PrimeCountingFunction(ulong, CancellationToken)"/> answer cannot isolate a
/// miscount in one window, because errors in separate windows can cancel.</para>
/// </remarks>
public static partial class PrimeLeafKernels {
    internal const int CombinatorialTinyPrimeCount = 8;
    internal const uint CombinatorialTinyPeriod = 9_699_690;
    internal const uint CombinatorialTinyTotient = 1_658_880;
    internal const ushort CombinatorialFactorMask = 0x7fff;
    internal const ushort CombinatorialNegativeMoebius = 0x8000;
    // The widest leaf-sieve segment, in integers, that a prefix cursor reads.
    internal const uint CombinatorialPrefixWidthLimit = (1U << 20);

    // The leaf sieve's counter slots per block, sixteen bits each. A block's count is the sum of its slots modulo
    // 2^16; a slot alone may wrap. Counted crossing k of a wheel cycle decrements slot k%2, so crossings that share a
    // slot are two wheel steps apart, at least 6q bytes for the prime 30q+r, and share a 64-byte block, chaining their
    // read-modify-writes, only for q<=10, p<320. A periodic block kernel writes any split of the block's count and a
    // recount writes it into slot 0. Two slots make a block's counters one 32-bit word, so one vector of counters
    // covers as many blocks as it has 32-bit lanes.
    private const int CombinatorialCounterSlots = 2;
    // A leaf-sieve counter block holds eight words, 2^CombinatorialCounterShift: one 512-bit vector, two 256-bit or
    // four 128-bit, so a prefix cursor counts the words before a query in its block through one 512-bit window. Its
    // 64 bytes hold at most 512 set bits, so a block's count fits sixteen bits. A 4096-word hard-leaf segment has 512
    // blocks.
    private const int CombinatorialCounterShift = 3;
    // The blocks a prefix cursor's block window sums: the 32-bit block counters that fill one 512-bit vector, 64
    // bytes, so sixteen; the AVX2 window sums the same blocks through two 256-bit vectors.
    private const int CombinatorialCounterWindow = (64 / (CombinatorialCounterSlots * sizeof(ushort)));
    // The blocks a periodic pass masks and recounts per step: two, 128 bytes, the stride its limit is derived from.
    private const int CombinatorialPeriodicStrideBlocks = 2;
    // The bytes of masks the leaf sieve's periodic pattern keeps beyond one period: one periodic stride.
    private const uint CombinatorialPeriodicOverhang = ((uint)(CombinatorialPeriodicStrideBlocks << (CombinatorialCounterShift + 3)));

    // The set bits of each four-bit value, repeated for every 128-bit lane of a 512-bit shuffle table.
    private static ReadOnlySpan<byte> CombinatorialNibbleBitCounts => [
        0, 1, 1, 2, 1, 2, 2, 3, 1, 2, 2, 3, 2, 3, 3, 4, 0, 1, 1, 2, 1, 2, 2, 3, 1, 2, 2, 3, 2, 3, 3, 4,
        0, 1, 1, 2, 1, 2, 2, 3, 1, 2, 2, 3, 2, 3, 3, 4, 0, 1, 1, 2, 1, 2, 2, 3, 1, 2, 2, 3, 2, 3, 3, 4,
    ];
    // The set bits of each seven-bit value: a two-table byte permute looks a byte's low seven bits up in one step.
    private static ReadOnlySpan<byte> CombinatorialSevenBitCounts => [
        0, 1, 1, 2, 1, 2, 2, 3, 1, 2, 2, 3, 2, 3, 3, 4, 1, 2, 2, 3, 2, 3, 3, 4, 2, 3, 3, 4, 3, 4, 4, 5,
        1, 2, 2, 3, 2, 3, 3, 4, 2, 3, 3, 4, 3, 4, 4, 5, 2, 3, 3, 4, 3, 4, 4, 5, 3, 4, 4, 5, 4, 5, 5, 6,
        1, 2, 2, 3, 2, 3, 3, 4, 2, 3, 3, 4, 3, 4, 4, 5, 2, 3, 3, 4, 3, 4, 4, 5, 3, 4, 4, 5, 4, 5, 5, 6,
        2, 3, 3, 4, 3, 4, 4, 5, 3, 4, 4, 5, 4, 5, 5, 6, 3, 4, 4, 5, 4, 5, 5, 6, 4, 5, 5, 6, 5, 6, 6, 7,
    ];
    // Byte j of the two-source permute that gathers each 64-bit SAD sum's low byte: sums 0..7 of the first block's
    // vector, then sums 0..7 of the second's, whose indices start at 64.
    private static ReadOnlySpan<byte> CombinatorialSadGather => [
        0, 8, 16, 24, 32, 40, 48, 56, 64, 72, 80, 88, 96, 104, 112, 120,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    ];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int CombinatorialCoordinate(uint value) {
        return (((((int)(value / PrimeWheel30.Modulus)) * PrimeWheel30.ChannelCount)
            + System.Numerics.BitOperations.PopCount(value: CombinatorialTables.PrefixMasks[((int)(value % PrimeWheel30.Modulus))])) - 1);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint CombinatorialNumber(int coordinate) {
        return (((((uint)coordinate) >> 3) * PrimeWheel30.Modulus) + PrimeWheel30.NumericResidues[coordinate & (PrimeWheel30.ChannelCount - 1)]);
    }
}
