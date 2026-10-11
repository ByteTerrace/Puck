using System.Numerics;

namespace Puck.Maths;

public static partial class PrimeLeafKernels {
    /// <summary>One step of a prime's walk over the thirty-wheel: the bit its next multiple lands on and how far the byte cursor moves.</summary>
    /// <remarks>Public so a law can compare the table with the wheel derivation entry by entry; the counting engine reads
    /// the steps through <see cref="CombinatorialTables.Steps"/>.</remarks>
    public readonly struct CombinatorialWheelStep {
        /// <summary>The numeric bit, from zero through seven, that the multiple lands on within its wheel byte.</summary>
        public readonly byte Bit;
        /// <summary>The multiplier gap from this phase to the next, multiplied by the prime's quotient to give the byte advance.</summary>
        public readonly byte Gap;
        /// <summary>The bytes beyond <c>quotient · Gap</c> that the step advances.</summary>
        public readonly byte Carry;

        /// <summary>Initializes a step.</summary>
        /// <param name="bit">The numeric bit the multiple lands on.</param>
        /// <param name="gap">The multiplier gap to the next phase.</param>
        /// <param name="carry">The carry beyond the quotient's share of the gap.</param>
        public CombinatorialWheelStep(byte bit, byte gap, byte carry) {
            Bit = bit;
            Gap = gap;
            Carry = carry;
        }
    }
    /// <summary>Holds the constant tables the combinatorial prime counter reads, each exposed as a read-only view.</summary>
    public static class CombinatorialTables {
        private static readonly CombinatorialWheelStep[] StepTable = CreateSteps();

        /// <summary>Gets, for every offset below <see cref="PrimeWheel30.WordIntegers"/>, the bits of a bitmap word that
        /// hold the units of thirty at or below it.</summary>
        /// <remarks>Entry <c>offset</c> equals <see cref="PrimeWheel30.WordPrefixMask(int)"/>. The values are written out
        /// as constant data, so a read costs one load from a constant address and no caller can overwrite them; the law
        /// <c>prime-exploration.wheel-tables-match-their-derivation</c> proves each equals the derivation.</remarks>
        public static ReadOnlySpan<ulong> PrefixMasks => [
        0x0000000000000000UL, 0x0000000000000001UL, 0x0000000000000001UL, 0x0000000000000001UL,
        0x0000000000000001UL, 0x0000000000000001UL, 0x0000000000000001UL, 0x0000000000000003UL,
        0x0000000000000003UL, 0x0000000000000003UL, 0x0000000000000003UL, 0x0000000000000007UL,
        0x0000000000000007UL, 0x000000000000000FUL, 0x000000000000000FUL, 0x000000000000000FUL,
        0x000000000000000FUL, 0x000000000000001FUL, 0x000000000000001FUL, 0x000000000000003FUL,
        0x000000000000003FUL, 0x000000000000003FUL, 0x000000000000003FUL, 0x000000000000007FUL,
        0x000000000000007FUL, 0x000000000000007FUL, 0x000000000000007FUL, 0x000000000000007FUL,
        0x000000000000007FUL, 0x00000000000000FFUL, 0x00000000000000FFUL, 0x00000000000001FFUL,
        0x00000000000001FFUL, 0x00000000000001FFUL, 0x00000000000001FFUL, 0x00000000000001FFUL,
        0x00000000000001FFUL, 0x00000000000003FFUL, 0x00000000000003FFUL, 0x00000000000003FFUL,
        0x00000000000003FFUL, 0x00000000000007FFUL, 0x00000000000007FFUL, 0x0000000000000FFFUL,
        0x0000000000000FFFUL, 0x0000000000000FFFUL, 0x0000000000000FFFUL, 0x0000000000001FFFUL,
        0x0000000000001FFFUL, 0x0000000000003FFFUL, 0x0000000000003FFFUL, 0x0000000000003FFFUL,
        0x0000000000003FFFUL, 0x0000000000007FFFUL, 0x0000000000007FFFUL, 0x0000000000007FFFUL,
        0x0000000000007FFFUL, 0x0000000000007FFFUL, 0x0000000000007FFFUL, 0x000000000000FFFFUL,
        0x000000000000FFFFUL, 0x000000000001FFFFUL, 0x000000000001FFFFUL, 0x000000000001FFFFUL,
        0x000000000001FFFFUL, 0x000000000001FFFFUL, 0x000000000001FFFFUL, 0x000000000003FFFFUL,
        0x000000000003FFFFUL, 0x000000000003FFFFUL, 0x000000000003FFFFUL, 0x000000000007FFFFUL,
        0x000000000007FFFFUL, 0x00000000000FFFFFUL, 0x00000000000FFFFFUL, 0x00000000000FFFFFUL,
        0x00000000000FFFFFUL, 0x00000000001FFFFFUL, 0x00000000001FFFFFUL, 0x00000000003FFFFFUL,
        0x00000000003FFFFFUL, 0x00000000003FFFFFUL, 0x00000000003FFFFFUL, 0x00000000007FFFFFUL,
        0x00000000007FFFFFUL, 0x00000000007FFFFFUL, 0x00000000007FFFFFUL, 0x00000000007FFFFFUL,
        0x00000000007FFFFFUL, 0x0000000000FFFFFFUL, 0x0000000000FFFFFFUL, 0x0000000001FFFFFFUL,
        0x0000000001FFFFFFUL, 0x0000000001FFFFFFUL, 0x0000000001FFFFFFUL, 0x0000000001FFFFFFUL,
        0x0000000001FFFFFFUL, 0x0000000003FFFFFFUL, 0x0000000003FFFFFFUL, 0x0000000003FFFFFFUL,
        0x0000000003FFFFFFUL, 0x0000000007FFFFFFUL, 0x0000000007FFFFFFUL, 0x000000000FFFFFFFUL,
        0x000000000FFFFFFFUL, 0x000000000FFFFFFFUL, 0x000000000FFFFFFFUL, 0x000000001FFFFFFFUL,
        0x000000001FFFFFFFUL, 0x000000003FFFFFFFUL, 0x000000003FFFFFFFUL, 0x000000003FFFFFFFUL,
        0x000000003FFFFFFFUL, 0x000000007FFFFFFFUL, 0x000000007FFFFFFFUL, 0x000000007FFFFFFFUL,
        0x000000007FFFFFFFUL, 0x000000007FFFFFFFUL, 0x000000007FFFFFFFUL, 0x00000000FFFFFFFFUL,
        0x00000000FFFFFFFFUL, 0x00000001FFFFFFFFUL, 0x00000001FFFFFFFFUL, 0x00000001FFFFFFFFUL,
        0x00000001FFFFFFFFUL, 0x00000001FFFFFFFFUL, 0x00000001FFFFFFFFUL, 0x00000003FFFFFFFFUL,
        0x00000003FFFFFFFFUL, 0x00000003FFFFFFFFUL, 0x00000003FFFFFFFFUL, 0x00000007FFFFFFFFUL,
        0x00000007FFFFFFFFUL, 0x0000000FFFFFFFFFUL, 0x0000000FFFFFFFFFUL, 0x0000000FFFFFFFFFUL,
        0x0000000FFFFFFFFFUL, 0x0000001FFFFFFFFFUL, 0x0000001FFFFFFFFFUL, 0x0000003FFFFFFFFFUL,
        0x0000003FFFFFFFFFUL, 0x0000003FFFFFFFFFUL, 0x0000003FFFFFFFFFUL, 0x0000007FFFFFFFFFUL,
        0x0000007FFFFFFFFFUL, 0x0000007FFFFFFFFFUL, 0x0000007FFFFFFFFFUL, 0x0000007FFFFFFFFFUL,
        0x0000007FFFFFFFFFUL, 0x000000FFFFFFFFFFUL, 0x000000FFFFFFFFFFUL, 0x000001FFFFFFFFFFUL,
        0x000001FFFFFFFFFFUL, 0x000001FFFFFFFFFFUL, 0x000001FFFFFFFFFFUL, 0x000001FFFFFFFFFFUL,
        0x000001FFFFFFFFFFUL, 0x000003FFFFFFFFFFUL, 0x000003FFFFFFFFFFUL, 0x000003FFFFFFFFFFUL,
        0x000003FFFFFFFFFFUL, 0x000007FFFFFFFFFFUL, 0x000007FFFFFFFFFFUL, 0x00000FFFFFFFFFFFUL,
        0x00000FFFFFFFFFFFUL, 0x00000FFFFFFFFFFFUL, 0x00000FFFFFFFFFFFUL, 0x00001FFFFFFFFFFFUL,
        0x00001FFFFFFFFFFFUL, 0x00003FFFFFFFFFFFUL, 0x00003FFFFFFFFFFFUL, 0x00003FFFFFFFFFFFUL,
        0x00003FFFFFFFFFFFUL, 0x00007FFFFFFFFFFFUL, 0x00007FFFFFFFFFFFUL, 0x00007FFFFFFFFFFFUL,
        0x00007FFFFFFFFFFFUL, 0x00007FFFFFFFFFFFUL, 0x00007FFFFFFFFFFFUL, 0x0000FFFFFFFFFFFFUL,
        0x0000FFFFFFFFFFFFUL, 0x0001FFFFFFFFFFFFUL, 0x0001FFFFFFFFFFFFUL, 0x0001FFFFFFFFFFFFUL,
        0x0001FFFFFFFFFFFFUL, 0x0001FFFFFFFFFFFFUL, 0x0001FFFFFFFFFFFFUL, 0x0003FFFFFFFFFFFFUL,
        0x0003FFFFFFFFFFFFUL, 0x0003FFFFFFFFFFFFUL, 0x0003FFFFFFFFFFFFUL, 0x0007FFFFFFFFFFFFUL,
        0x0007FFFFFFFFFFFFUL, 0x000FFFFFFFFFFFFFUL, 0x000FFFFFFFFFFFFFUL, 0x000FFFFFFFFFFFFFUL,
        0x000FFFFFFFFFFFFFUL, 0x001FFFFFFFFFFFFFUL, 0x001FFFFFFFFFFFFFUL, 0x003FFFFFFFFFFFFFUL,
        0x003FFFFFFFFFFFFFUL, 0x003FFFFFFFFFFFFFUL, 0x003FFFFFFFFFFFFFUL, 0x007FFFFFFFFFFFFFUL,
        0x007FFFFFFFFFFFFFUL, 0x007FFFFFFFFFFFFFUL, 0x007FFFFFFFFFFFFFUL, 0x007FFFFFFFFFFFFFUL,
        0x007FFFFFFFFFFFFFUL, 0x00FFFFFFFFFFFFFFUL, 0x00FFFFFFFFFFFFFFUL, 0x01FFFFFFFFFFFFFFUL,
        0x01FFFFFFFFFFFFFFUL, 0x01FFFFFFFFFFFFFFUL, 0x01FFFFFFFFFFFFFFUL, 0x01FFFFFFFFFFFFFFUL,
        0x01FFFFFFFFFFFFFFUL, 0x03FFFFFFFFFFFFFFUL, 0x03FFFFFFFFFFFFFFUL, 0x03FFFFFFFFFFFFFFUL,
        0x03FFFFFFFFFFFFFFUL, 0x07FFFFFFFFFFFFFFUL, 0x07FFFFFFFFFFFFFFUL, 0x0FFFFFFFFFFFFFFFUL,
        0x0FFFFFFFFFFFFFFFUL, 0x0FFFFFFFFFFFFFFFUL, 0x0FFFFFFFFFFFFFFFUL, 0x1FFFFFFFFFFFFFFFUL,
        0x1FFFFFFFFFFFFFFFUL, 0x3FFFFFFFFFFFFFFFUL, 0x3FFFFFFFFFFFFFFFUL, 0x3FFFFFFFFFFFFFFFUL,
        0x3FFFFFFFFFFFFFFFUL, 0x7FFFFFFFFFFFFFFFUL, 0x7FFFFFFFFFFFFFFFUL, 0x7FFFFFFFFFFFFFFFUL,
        0x7FFFFFFFFFFFFFFFUL, 0x7FFFFFFFFFFFFFFFUL, 0x7FFFFFFFFFFFFFFFUL, 0xFFFFFFFFFFFFFFFFUL,
        ];
        /// <summary>Gets the wheel step for every prime channel and multiplier phase.</summary>
        /// <remarks>Step <c>channel · ChannelCount + phase</c> carries the numeric bit, byte gap and byte carry
        /// <see cref="PrimeWheel30"/> derives for that pair. The view is read-only: no caller can change a step.</remarks>
        public static ReadOnlySpan<CombinatorialWheelStep> Steps => StepTable;

        internal static readonly ulong[] TinyWords = CreateTinyWords();
        internal static readonly uint[] TinyPrefix = CreateTinyPrefix();

        private static CombinatorialWheelStep[] CreateSteps() {
            var steps = new CombinatorialWheelStep[(PrimeWheel30.ChannelCount * PrimeWheel30.ChannelCount)];

            for (var channel = 0; (channel < PrimeWheel30.ChannelCount); ++channel) {
                var residue = PrimeWheel30.Residues[channel];

                for (var phase = 0; (phase < PrimeWheel30.ChannelCount); ++phase) {
                    steps[((channel * PrimeWheel30.ChannelCount) + phase)] = new(bit: ((byte)PrimeWheel30.TargetBit(phase: phase, residue: residue)),
                        carry: ((byte)PrimeWheel30.Carry(phase: phase, residue: residue)), gap: ((byte)PrimeWheel30.Gap(phase: phase)));
                }
            }
            return steps;
        }
        private static ulong[] CreateTinyWords() {
            var words = new ulong[(((CombinatorialTinyPeriod + PrimeWheel30.WordIntegers) - 1U) / PrimeWheel30.WordIntegers)];

            Array.Fill(array: words, value: ulong.MaxValue);
            words[^1] &= PrefixMasks[((int)((CombinatorialTinyPeriod - 1U) % PrimeWheel30.WordIntegers))];

            ReadOnlySpan<uint> primes = [7, 11, 13, 17, 19];

            foreach (var prime in primes) {
                _ = PrimeWheel30.TryChannel(channel: out var channel, residue: ((byte)prime));

                var quotient = (prime / PrimeWheel30.Modulus);
                var cursor = quotient;
                var phase = 0;
                var steps = StepTable.AsSpan(length: PrimeWheel30.ChannelCount, start: (channel * PrimeWheel30.ChannelCount));

                while (cursor < (CombinatorialTinyPeriod / PrimeWheel30.Modulus)) {
                    var step = steps[phase];
                    var bit = ((((int)(cursor % PrimeWheel30.ChannelCount)) * PrimeWheel30.ChannelCount) + step.Bit);

                    words[(cursor / PrimeWheel30.ChannelCount)] &= ~(1UL << bit);
                    cursor += ((quotient * step.Gap) + step.Carry);
                    phase = (phase + 1) & (PrimeWheel30.ChannelCount - 1);
                }
            }
            return words;
        }
        private static uint[] CreateTinyPrefix() {
            var prefix = new uint[TinyWords.Length];
            var count = 0U;

            for (var index = 0; (index < prefix.Length); ++index) {
                prefix[index] = count;
                count += ((uint)BitOperations.PopCount(value: TinyWords[index]));
            }
            return prefix;
        }
    }
}
