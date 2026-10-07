using BenchmarkDotNet.Attributes;
using Puck.Maths;
using System.Runtime.CompilerServices;

namespace Puck.Cli.Bench;

[MemoryDiagnoser]
public class NthPrimeRequests {
    [Params(10U, 1_000U, 6_542U, 1_000_000U, 100_000_000U)]
    public uint Ordinal { get; set; }

    [GlobalSetup]
    public void Setup() {
        var expected = Ordinal switch {
            10 => 29U,
            1_000 => 7_919U,
            6_542 => 65_521U,
            1_000_000 => 15_485_863U,
            100_000_000 => 2_038_074_743U,
            _ => throw new InvalidOperationException(),
        };

        if (Select() != expected) { throw new InvalidOperationException(message: "Nth-prime request differs from its exact reference."); }
    }
    [Benchmark]
    public uint Select() => (Ordinal - 1U).NthPrime();
}
[MemoryDiagnoser]
public class RandomPrimeRequests {
    private const int Requests = 128;
    private const int DrawBudget = 2048;

    private ulong m_low;
    private ulong m_high;

    [Params("Small", "UInt32", "UInt64", "HighWindow")]
    public string Band { get; set; } = "Small";

    [GlobalSetup]
    public void Setup() {
        (m_low, m_high) = Band switch {
            "Small" => (2UL, 65_535UL),
            "UInt32" => ((1UL << 31), uint.MaxValue),
            "UInt64" => ((1UL << 63), ulong.MaxValue),
            "HighWindow" => (1_000_000_000_000_000_000UL, 1_000_000_000_000_999_999UL),
            _ => throw new InvalidOperationException(),
        };
        for (var method = 0; (method < 3); ++method) {
            var generator = Pcg32XshRr.Create(state: 42, stream: 54);

            for (var request = 0; (request < Requests); ++request) {
                var found = ((method == 2)
                    ? PrimeExploration.TryRandomPrime(generator: ref generator, high: m_high, low: m_low, maxAttempts: DrawBudget, prime: out var prime)
                    : ((method == 1) ? IntegerRejection<BaillieDecision>(generator: ref generator, high: m_high, low: m_low, prime: out prime)
                        : IntegerRejection<MillerDecision>(generator: ref generator, high: m_high, low: m_low, prime: out prime)));

                if (!found || (prime < m_low) || (prime > m_high) || !PrimeExplorationBenchmarkReference.IsPrimeCandidate(value: prime)) {
                    throw new InvalidOperationException(message: "Random-prime request failed its independent exact check.");
                }
            }
        }
    }
    [Benchmark(Baseline = true, OperationsPerInvoke = Requests)]
    public ulong IntegerMillerRabin() => Run<MillerRequest>();
    [Benchmark(OperationsPerInvoke = Requests)]
    public ulong IntegerBailliePsw() => Run<BaillieRequest>();
    [Benchmark(OperationsPerInvoke = Requests)]
    public ulong Select() => Run<SelectionRequest>();

    private ulong Run<TRequest>() where TRequest : struct, IRequest {
        var generator = Pcg32XshRr.Create(state: 42, stream: 54);
        var checksum = 0UL;

        for (var request = 0; (request < Requests); ++request) {
            var found = TRequest.Try(generator: ref generator, high: m_high, low: m_low, prime: out var prime);

            if (!found) { throw new InvalidOperationException(message: "Random-prime request exhausted its budget."); }
            checksum = System.Numerics.BitOperations.RotateLeft(offset: 3, value: checksum) ^ prime;
        }
        return checksum;
    }
    private static bool IntegerRejection<TDecision>(ulong low, ulong high, ref Pcg32XshRr generator, out ulong prime)
        where TDecision : struct, IDecision {
        var width = ((high - low) + 1);
        var threshold = (unchecked((0UL - width)) % width);

        for (var attempt = 0; (attempt < DrawBudget); ++attempt) {
            var word = (((ulong)generator.NextUInt32()) << 32) | generator.NextUInt32();
            var product = (((UInt128)word) * width);

            if (unchecked((ulong)product) < threshold) { continue; }
            var value = (low + ((ulong)(product >> 64)));
            var accepted = ((value <= uint.MaxValue) ? ((uint)value).IsPrime()
                : TDecision.IsPrime(value: value));

            if (accepted) { prime = value; return true; }
        }
        prime = 0;
        return false;
    }

    private interface IRequest {
        static abstract bool Try(ulong low, ulong high, ref Pcg32XshRr generator, out ulong prime);
    }
    private interface IDecision {
        static abstract bool IsPrime(ulong value);
    }
    private readonly struct MillerRequest : IRequest {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Try(ulong low, ulong high, ref Pcg32XshRr generator, out ulong prime) => IntegerRejection<MillerDecision>(generator: ref generator, high: high, low: low, prime: out prime);
    }
    private readonly struct BaillieRequest : IRequest {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Try(ulong low, ulong high, ref Pcg32XshRr generator, out ulong prime) => IntegerRejection<BaillieDecision>(generator: ref generator, high: high, low: low, prime: out prime);
    }
    private readonly struct SelectionRequest : IRequest {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Try(ulong low, ulong high, ref Pcg32XshRr generator, out ulong prime) => PrimeExploration.TryRandomPrime(generator: ref generator, high: high, low: low, maxAttempts: DrawBudget, prime: out prime);
    }
    private readonly struct MillerDecision : IDecision {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsPrime(ulong value) => PrimeExploration.IsPrime(value: value);
    }
    private readonly struct BaillieDecision : IDecision {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsPrime(ulong value) => PrimeField64.IsBaillieProbablePrime(value: value);
    }
}
