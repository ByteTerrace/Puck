using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using Puck.Maths;

namespace Puck.Cli.Bench;

public enum PrimeBenchmarkOutput { Checksum, Buffer }
public enum PrimeMaskOrder { Grouped, Mixed }
[MemoryDiagnoser]
public class PrimeCandidateScan {
    private const int Count = 4096;

    private readonly ulong[] m_output = new ulong[Count];

    [Params(0UL, 1_000_000_000_000UL, 614891469123650696UL)]
    public ulong StartBlock { get; set; }
    [Params(PrimeBenchmarkOutput.Checksum, PrimeBenchmarkOutput.Buffer)]
    public PrimeBenchmarkOutput Output { get; set; }

    [GlobalSetup]
    public void Setup() {
        var coordinates = new PrimeBenchmarkLoops.AlgebraicStepper(block: StartBlock);
        var gaps = new PrimeBenchmarkLoops.NumericStepper(block: StartBlock);
        ReadOnlySpan<byte> expectedResidues = [1, 7, 11, 13, 17, 19, 23, 29];

        for (var i = 0; (i < Count); ++i) {
            var value = (((StartBlock + ((ulong)(i / 8))) * 30) + expectedResidues[i & 7]);

            if ((coordinates.Next() != value) || (gaps.Next() != value)) {
                throw new InvalidOperationException(message: "Candidate scan sequences disagree.");
            }
        }
        // Check every stored value, independently of the checksum, including the final partial-word window.
        foreach (var algebraic in new[] { false, true }) {
            if (algebraic) { _ = PrimeBenchmarkLoops.Scan(new PrimeBenchmarkLoops.AlgebraicStepper(block: StartBlock), new PrimeBenchmarkLoops.WordBuffer(output: m_output), Count); } else { _ = PrimeBenchmarkLoops.Scan(new PrimeBenchmarkLoops.NumericStepper(block: StartBlock), new PrimeBenchmarkLoops.WordBuffer(output: m_output), Count); }
            for (var i = 0; (i < Count); ++i) {
                var value = (((StartBlock + ((ulong)(i / 8))) * 30) + expectedResidues[i & 7]);

                if (m_output[i] != value) { throw new InvalidOperationException(message: "Candidate scan buffer disagrees."); }
            }
        }
        if (AlgebraicCoordinates() != NumericGaps()) {
            throw new InvalidOperationException(message: "Candidate scan checksums disagree.");
        }
    }
    [Benchmark(OperationsPerInvoke = Count)]
    public ulong AlgebraicCoordinates() => ((Output == PrimeBenchmarkOutput.Buffer)
        ? PrimeBenchmarkLoops.Scan(new PrimeBenchmarkLoops.AlgebraicStepper(block: StartBlock), new PrimeBenchmarkLoops.WordBuffer(output: m_output), Count)
        : PrimeBenchmarkLoops.Scan(new PrimeBenchmarkLoops.AlgebraicStepper(block: StartBlock), new PrimeBenchmarkLoops.Checksum(), Count));
    [Benchmark(Baseline = true, OperationsPerInvoke = Count)]
    public ulong NumericGaps() => ((Output == PrimeBenchmarkOutput.Buffer)
        ? PrimeBenchmarkLoops.Scan(new PrimeBenchmarkLoops.NumericStepper(block: StartBlock), new PrimeBenchmarkLoops.WordBuffer(output: m_output), Count)
        : PrimeBenchmarkLoops.Scan(new PrimeBenchmarkLoops.NumericStepper(block: StartBlock), new PrimeBenchmarkLoops.Checksum(), Count));
}
[MemoryDiagnoser]
public class PrimeChannelMasks {
    private const int Count = (8 * 256);

    private readonly byte[] m_table = new byte[Count];
    private readonly ushort[] m_inputs = new ushort[Count];
    private readonly byte[] m_output = new byte[Count];

    [Params(PrimeMaskOrder.Grouped, PrimeMaskOrder.Mixed)]
    public PrimeMaskOrder Order { get; set; }
    [Params(PrimeBenchmarkOutput.Checksum, PrimeBenchmarkOutput.Buffer)]
    public PrimeBenchmarkOutput Output { get; set; }

    [GlobalSetup]
    public void Setup() {
        for (byte channel = 0; (channel < 8); ++channel) {
            for (var mask = 0; (mask < 256); ++mask) {
                var expected = DirectPermutation(channel: channel, mask: ((byte)mask));

                m_table[((channel * 256) + mask)] = expected;
                m_inputs[((channel * 256) + mask)] = ((ushort)((channel << 8) | mask));
                if (PrimeWheel30.PermuteMask(channel: channel, mask: ((byte)mask)) != expected) {
                    throw new InvalidOperationException(message: "Channel mask permutations disagree.");
                }
            }
        }
        if (Order == PrimeMaskOrder.Mixed) {
            // A fixed Fisher-Yates permutation preserves all 2048 operands, changing only access order.
            uint state = 0x6A09E667;

            for (var i = (Count - 1); (i > 0); --i) {
                state ^= (state << 13);
                state ^= (state >> 17);
                state ^= (state << 5);
                var other = ((int)(state % ((uint)(i + 1))));

                (m_inputs[i], m_inputs[other]) = (m_inputs[other], m_inputs[i]);
            }
        }
        CheckBuffer<Rotations>();
        CheckBuffer<Lookup>();
        CheckBuffer<ResidueProducts>();
        if ((NibbleRotations() != PermutationTable()) || (NibbleRotations() != DirectResidues())) {
            throw new InvalidOperationException(message: "Channel mask checksums disagree.");
        }
    }
    [Benchmark(OperationsPerInvoke = Count)]
    public ulong NibbleRotations() => Run<Rotations>();
    [Benchmark(Baseline = true, OperationsPerInvoke = Count)]
    public ulong PermutationTable() => Run<Lookup>();
    [Benchmark(OperationsPerInvoke = Count)]
    public ulong DirectResidues() => Run<ResidueProducts>();

    private ulong Run<T>() where T : struct, PrimeBenchmarkLoops.IPermutation => ((Output == PrimeBenchmarkOutput.Buffer)
        ? PrimeBenchmarkLoops.Masks<T, PrimeBenchmarkLoops.ByteBuffer>(m_inputs, m_table, new PrimeBenchmarkLoops.ByteBuffer(output: m_output))
        : PrimeBenchmarkLoops.Masks<T, PrimeBenchmarkLoops.Checksum>(m_inputs, m_table, new PrimeBenchmarkLoops.Checksum()));
    private void CheckBuffer<T>() where T : struct, PrimeBenchmarkLoops.IPermutation {
        _ = PrimeBenchmarkLoops.Masks<T, PrimeBenchmarkLoops.ByteBuffer>(m_inputs, m_table, new PrimeBenchmarkLoops.ByteBuffer(output: m_output));
        for (var i = 0; (i < Count); ++i) {
            var operand = m_inputs[i];

            if (m_output[i] != DirectPermutation(channel: ((byte)(operand >> 8)), mask: ((byte)operand))) {
                throw new InvalidOperationException(message: "Channel mask buffer disagrees.");
            }
        }
    }

    private readonly struct Rotations : PrimeBenchmarkLoops.IPermutation {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte Transform(byte mask, byte channel, ReadOnlySpan<byte> table) => PrimeWheel30.PermuteMask(mask: mask, rotation: channel & 3, swap: ((channel & 4) != 0));
    }
    private readonly struct Lookup : PrimeBenchmarkLoops.IPermutation {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte Transform(byte mask, byte channel, ReadOnlySpan<byte> table) =>
            // Masks checks the table length; its byte mask and masked channel prove the offset is in 0..2047.
            Unsafe.Add(ref MemoryMarshal.GetReference(span: table), ((channel * 256) + mask));
    }
    private readonly struct ResidueProducts : PrimeBenchmarkLoops.IPermutation {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte Transform(byte mask, byte channel, ReadOnlySpan<byte> table) => DirectPermutation(channel: channel, mask: mask);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte DirectPermutation(byte mask, byte channel) {
        var output = ((byte)0);
        var residues = PrimeWheel30.Residues;

        for (var source = 0; (source < 8); ++source) {
            if ((mask & (1 << source)) != 0) {
                var destinationResidue = ((residues[source] * residues[channel]) % 30);

                for (var destination = 0; (destination < 8); ++destination) {
                    if (residues[destination] == destinationResidue) {
                        output |= ((byte)(1 << destination));
                        break;
                    }
                }
            }
        }
        return output;
    }
}
[MemoryDiagnoser]
public class PrimePrimality {
    private const int Count = 128;

    private CandidateAddress[] m_candidates = [];

    [Params("Mixed", "PrimeHeavy")]
    public string Workload { get; set; } = "Mixed";
    [Params(0, 4, 16)]
    public int TrialDivisors { get; set; }

    [GlobalSetup]
    public void Setup() {
        m_candidates = new CandidateAddress[Count];
        var lowValue = 30_000_000_000_001UL;
        var highValue = 18_446_744_073_679_551_601UL;
        var lowPosition = 0;
        var highPosition = 0;

        for (var i = 0; (i < Count); ++i) {
            var useLow = ((i & 1) == 0);
            var value = (useLow ? lowValue : highValue);
            var position = (useLow ? lowPosition : highPosition);

            if (Workload == "PrimeHeavy") {
                while (!PrimeExplorationBenchmarkReference.IsPrimeCandidate(value: value)) {
                    PrimeExplorationBenchmarkReference.Advance(position: ref position, value: ref value);
                }
            }
            if ((value <= uint.MaxValue) || !CandidateAddress.TryFromValue(value, out m_candidates[i])) {
                throw new InvalidOperationException(message: "Primality benchmark requires wide wheel candidates.");
            }
            var expected = PrimeExplorationBenchmarkReference.IsPrimeCandidate(value: value);

            PrimeExplorationBenchmarkReference.CheckPowers(modulus: value);
            if ((PrimeExploration.IsPrimeCandidate(candidate: m_candidates[i]) != expected) ||
                (!PrimeExplorationBenchmarkReference.PassesTrialDivision(value, TrialDivisors) && expected)) {
                throw new InvalidOperationException(message: "Primality benchmark decisions disagree.");
            }
            PrimeExplorationBenchmarkReference.Advance(position: ref position, value: ref value);
            if (useLow) {
                lowValue = value;
                lowPosition = position;
            } else {
                highValue = value;
                highPosition = position;
            }
        }
        if (ProductionCandidate() != UInt128Reference()) {
            throw new InvalidOperationException(message: "Primality benchmark checksums disagree.");
        }
    }
    [Benchmark(OperationsPerInvoke = Count)]
    public ulong ProductionCandidate() => PrimeBenchmarkLoops.Decisions<Production>(candidates: m_candidates, trialDivisors: TrialDivisors);
    [Benchmark(Baseline = true, OperationsPerInvoke = Count)]
    public ulong UInt128Reference() => PrimeBenchmarkLoops.Decisions<Division>(candidates: m_candidates, trialDivisors: TrialDivisors);

    private readonly struct Production : PrimeBenchmarkLoops.IDecision {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsPrime(CandidateAddress candidate) => PrimeExploration.IsPrimeCandidate(candidate: candidate);
    }
    private readonly struct Division : PrimeBenchmarkLoops.IDecision {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsPrime(CandidateAddress candidate) => PrimeExplorationBenchmarkReference.IsPrimeCandidate(value: candidate.Value);
    }
}
[MemoryDiagnoser]
public class PrimeSieveSegments {
    private ulong m_low;
    private ulong m_high;
    private PrimeSieveMode m_mode;
    private ulong m_count;
    private Action<ulong> m_callback = null!;

    [Params(4096, 32768, 262144)]
    public int SegmentBytes { get; set; }
    [Params(PrimeSieveStrategy.WheelSteps, PrimeSieveStrategy.EightStreams, PrimeSieveStrategy.UnrolledPackets,
        PrimeSieveStrategy.CarriedPackets, PrimeSieveStrategy.CacheBlockedPackets, PrimeSieveStrategy.SpecializedPackets, PrimeSieveStrategy.PhaseSortedPackets, PrimeSieveStrategy.BucketPackets)]
    public PrimeSieveStrategy Strategy { get; set; }
    [Params(PrimeByteLayout.Numeric, PrimeByteLayout.Algebraic)]
    public PrimeByteLayout Layout { get; set; }
    [Params("Small", "High")]
    public string Interval { get; set; } = "Small";
    [Params(false, true)]
    public bool UsePreSieve { get; set; }

    [GlobalSetup]
    public void Setup() {
        m_callback = _ => ++m_count;
        m_low = ((Interval == "Small") ? 0UL : 1_000_000_000_000UL);
        m_high = ((Interval == "Small") ? 1_000_000UL : (m_low + 30_000UL));
        m_mode = ((Interval == "Small") ? PrimeSieveMode.Eratosthenes : PrimeSieveMode.Presieve);
        var expectedCount = 0UL;
        var expectedChecksum = 0UL;

        if (Interval == "Small") {
            expectedCount = 78498;
            var composite = new bool[1_000_001];

            for (var divisor = 2; (divisor <= 1000); ++divisor) {
                if (!composite[divisor]) {
                    for (var multiple = (divisor * divisor); (multiple < composite.Length); multiple += divisor) {
                        composite[multiple] = true;
                    }
                }
            }
            var observedCount = 0UL;

            for (var value = 2; (value < composite.Length); ++value) {
                if (!composite[value]) {
                    ++observedCount;
                    expectedChecksum = BitOperations.RotateLeft(offset: 3, value: expectedChecksum) ^ ((ulong)value);
                }
            }
            if (observedCount != expectedCount) {
                throw new InvalidOperationException(message: "Independent sieve count disagrees with pi(1000000).");
            }
        } else {
            for (var value = m_low; (value <= m_high); ++value) {
                if (CandidateAddress.TryFromValue(address: out _, value: value) && PrimeExplorationBenchmarkReference.IsPrimeCandidate(value: value)) {
                    ++expectedCount;
                    expectedChecksum = BitOperations.RotateLeft(offset: 3, value: expectedChecksum) ^ value;
                }
            }
        }
        var actualCount = 0UL;
        var actualChecksum = 0UL;

        PrimeExploration.Enumerate(m_low, m_high, prime => {
            ++actualCount;
            actualChecksum = BitOperations.RotateLeft(offset: 3, value: actualChecksum) ^ prime;
        }, SegmentBytes, Strategy, Layout, m_mode, UsePreSieve);
        if ((actualCount != expectedCount) || (actualChecksum != expectedChecksum) || (CountPrimes() != expectedCount) || (EnumeratePrimes() != expectedCount)) {
            throw new InvalidOperationException(message: "Segmented sieve benchmark disagrees with its independent reference.");
        }
    }
    [Benchmark]
    public ulong CountPrimes() => PrimeExploration.Count(m_low, m_high, SegmentBytes, Strategy, Layout, m_mode, UsePreSieve);
    [Benchmark]
    public ulong EnumeratePrimes() {
        m_count = 0;
        PrimeExploration.Enumerate(m_low, m_high, m_callback, SegmentBytes, Strategy, Layout, m_mode, UsePreSieve);
        return m_count;
    }
}

// Value-type specializations share traversal and output code. The JIT must inline their tiny operations;
// disassembly, rather than the interface syntax, establishes the absence of calls in the measured loops.
internal static class PrimeBenchmarkLoops {
    internal interface IStepper { ulong Next(); }
    internal interface ISink {
        void ValidateLength(int count);
        void Append(int index, ulong value);

        ulong Result { get; }
    }
    internal interface IPermutation { static abstract byte Transform(byte mask, byte channel, ReadOnlySpan<byte> table); }
    internal interface IDecision { static abstract bool IsPrime(CandidateAddress candidate); }
    internal struct Checksum : ISink {
        public ulong Result { get; private set; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ValidateLength(int count) { }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Append(int index, ulong value) => Result = BitOperations.RotateLeft(Result, 3) ^ value;
    }
    internal readonly struct WordBuffer(ulong[] output) : ISink {
        public ulong Result => output[^1];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ValidateLength(int count) {
            if (output.Length != count) { throw new ArgumentException(message: "Output length must match input count.", paramName: nameof(output)); }
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Append(int index, ulong value) => Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(array: output), index) = value;
    }
    internal readonly struct ByteBuffer(byte[] output) : ISink {
        public ulong Result => output[^1];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ValidateLength(int count) {
            if (output.Length != count) { throw new ArgumentException(message: "Output length must match input count.", paramName: nameof(output)); }
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Append(int index, ulong value) => Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(array: output), index) = ((byte)value);
    }
    internal struct NumericStepper(ulong block) : IStepper {
        private ulong m_value = ((block * 30) + 1);
        private int m_position;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong Next() {
            var value = m_value;

            m_value += PrimeExplorationBenchmarkReference.Gaps[m_position];
            m_position = (m_position + 1) & 7;
            return value;
        }
    }
    internal struct AlgebraicStepper(ulong block) : IStepper {
        private static ReadOnlySpan<byte> NextChannels => [1, 4, 7, 5, 3, 2, 0, 6];
        private static ReadOnlySpan<byte> Carries => [0, 0, 0, 0, 0, 0, 1, 0];

        private ulong m_block = block;
        private nint m_channel;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong Next() {
            // The complete cycle is checked in setup; the initial and every successor channel lie in 0..7.
            var channel = m_channel;
            var value = ((m_block * 30) + Unsafe.Add(ref MemoryMarshal.GetReference(span: PrimeWheel30.Residues), channel));

            m_block += Unsafe.Add(ref MemoryMarshal.GetReference(span: Carries), channel);
            m_channel = Unsafe.Add(ref MemoryMarshal.GetReference(span: NextChannels), channel);
            return value;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Scan<TStepper, TSink>(TStepper stepper, TSink sink, int count)
        where TStepper : struct, IStepper where TSink : struct, ISink {
        sink.ValidateLength(count: count);
        for (var i = 0; (i < count); ++i) { sink.Append(index: i, value: stepper.Next()); }
        return sink.Result;
    }
    internal static ulong Masks<TPermutation, TSink>(ReadOnlySpan<ushort> inputs, ReadOnlySpan<byte> table, TSink sink)
        where TPermutation : struct, IPermutation where TSink : struct, ISink {
        if (table.Length != 2048) { throw new ArgumentException(message: "Mask table must have 2048 entries.", paramName: nameof(table)); }
        sink.ValidateLength(count: inputs.Length);
        for (var i = 0; (i < inputs.Length); ++i) {
            var operand = inputs[i];
            // This common mask establishes the public operation's channel precondition for every specialization.
            var channel = ((byte)((operand >> 8) & 7));

            sink.Append(index: i, value: TPermutation.Transform(channel: channel, mask: ((byte)operand), table: table));
        }
        return sink.Result;
    }
    internal static ulong Decisions<T>(ReadOnlySpan<CandidateAddress> candidates, int trialDivisors) where T : struct, IDecision {
        var checksum = 0UL;
        var trialPrimes = PrimeExplorationBenchmarkReference.TrialPrimes[..trialDivisors];

        foreach (var candidate in candidates) {
            var value = candidate.Value;
            var prime = (PrimeExplorationBenchmarkReference.PassesTrialDivision(primes: trialPrimes, value: value) && T.IsPrime(candidate: candidate));

            checksum = BitOperations.RotateLeft(offset: 3, value: checksum) ^ (prime ? value : ~value);
        }
        return checksum;
    }
}
// Benchmark-only division baseline; production uses the shared Montgomery kernel.
internal static class PrimeExplorationBenchmarkReference {
    internal static ReadOnlySpan<byte> Gaps => [6, 4, 2, 4, 2, 4, 6, 2];
    internal static ReadOnlySpan<ulong> TrialPrimes => [7, 11, 13, 17, 19, 23, 29, 31, 37, 41, 43, 47, 53, 59, 61, 67];

    private static ReadOnlySpan<ulong> Witnesses => [2, 325, 9375, 28178, 450775, 9780504, 1795265022];

    internal static void Advance(ref ulong value, ref int position) {
        value += Gaps[position];
        position = (position + 1) & 7;
    }
    internal static bool PassesTrialDivision(ulong value, int count) => PassesTrialDivision(value: value, primes: TrialPrimes[..count]);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool PassesTrialDivision(ulong value, ReadOnlySpan<ulong> primes) {
        foreach (var prime in primes) {
            if ((value % prime) == 0) {
                return (value == prime);
            }
        }
        return true;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static bool IsPrimeCandidate(ulong value) {
        if (value < 2) {
            return false;
        }
        var oddPart = (value - 1);
        var exponent = BitOperations.TrailingZeroCount(value: oddPart);

        oddPart >>= exponent;
        foreach (var witness in Witnesses) {
            if (!PassesWitness(exponent: exponent, modulus: value, oddPart: oddPart, witness: witness)) { return false; }
        }
        return true;
    }

    private static bool PassesWitness(ulong modulus, ulong oddPart, int exponent, ulong witness) {
        var reduced = (witness % modulus);

        if (reduced == 0) { return true; }
        var power = Power(exponent: oddPart, modulus: modulus, value: reduced);
        var negativeOne = (modulus - 1);

        if ((power == 1) || (power == negativeOne)) { return true; }
        for (var round = 1; (round < exponent); ++round) {
            power = ((ulong)((((UInt128)power) * power) % modulus));
            if (power == negativeOne) { return true; }
        }
        return false;
    }
    // Match ScaledResidueRing64.Power's LSB-first schedule and call boundary. The round already reduced the
    // witness and established modulus > 1; the general public ModularPower API repeats those checks/reductions.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ulong Power(ulong value, ulong exponent, ulong modulus) {
        var power = value;
        var result = 1UL;

        while (exponent != 0) {
            if ((exponent & 1) != 0) { result = ((ulong)((((UInt128)result) * power) % modulus)); }
            exponent >>= 1;
            if (exponent != 0) { power = ((ulong)((((UInt128)power) * power) % modulus)); }
        }
        return result;
    }

    internal static void CheckPowers(ulong modulus) {
        var exponent = (modulus - 1);

        exponent >>= BitOperations.TrailingZeroCount(value: exponent);
        foreach (var witness in Witnesses) {
            var reduced = (witness % modulus);

            if (Power(exponent: exponent, modulus: modulus, value: reduced) != ((ulong)BigInteger.ModPow(exponent: exponent, modulus: modulus, value: reduced))) {
                throw new InvalidOperationException(message: "Division power disagrees with independent BigInteger arithmetic.");
            }
        }
    }
}
