using System.Numerics;
using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using Puck.Maths;

namespace Puck.Cli.Bench;

// Screen decision policies on identical unfiltered wheel candidates before measuring complete requests.
[MemoryDiagnoser]
public class PrimeFilterDecisions {
    private const int Count = 1024;

    private readonly CandidateAddress[] m_candidates = new CandidateAddress[Count];
    private ulong[] m_inverses = [];
    private ulong[] m_ceilings = [];

    [Params("1e12", "1e18", "U64High")]
    public string Band { get; set; } = "1e12";

    [Params(5, 59, 163, 541, 1021)]
    public int FilterLimit { get; set; }

    [GlobalSetup]
    public void Setup() {
        var (low, high) = Band switch {
            "1e12" => (1_000_000_000_000UL, 1_000_000_999_999UL),
            "1e18" => (1_000_000_000_000_000_000UL, 1_000_000_000_000_999_999UL),
            "U64High" => ((1UL << 63), ulong.MaxValue),
            _ => throw new InvalidOperationException(),
        };
        var primes = new List<ulong>();

        if (FilterLimit >= 7) { PrimeExploration.Enumerate(7, ((ulong)FilterLimit), primes.Add); }
        m_inverses = new ulong[primes.Count];
        m_ceilings = new ulong[primes.Count];
        for (var i = 0; (i < primes.Count); ++i) {
            m_inverses[i] = primes[i].ModularInverse();
            m_ceilings[i] = (ulong.MaxValue / primes[i]);
        }
        var generator = Pcg32XshRr.Create(state: 42, stream: 54);
        var width = ((high - low) + 1);
        var primeCount = 0;
        var survivors = 0;
        var identity = 0UL;

        for (var i = 0; (i < Count);) {
            var word = (((ulong)generator.NextUInt32()) << 32) | generator.NextUInt32();
            var value = (low + ((ulong)((((UInt128)word) * width) >> 64)));

            if (!CandidateAddress.TryFromValue(address: out var candidate, value: value)) { continue; }
            m_candidates[i++] = candidate;
            var expected = PrimeExplorationBenchmarkReference.IsPrimeCandidate(value: value);
            var survives = PassesFilter(value: value);

            if (survives) { ++survivors; }
            if (expected) { ++primeCount; }
            if (((survives && PrimeExploration.IsPrimeCandidate(candidate: candidate)) != expected) ||
                ((survives && PrimeField64.IsBaillieProbablePrime(value: value)) != expected)) {
                throw new InvalidOperationException(message: $"Filter/decision disagrees at {value}.");
            }
            identity = BitOperations.RotateLeft(offset: 3, value: identity) ^ value;
        }
        if (MillerRabin() != BailliePsw()) { throw new InvalidOperationException(message: "Filter checksums disagree."); }
        Console.WriteLine(value: $"filter screen: {Band}, through {FilterLimit}; {Count} candidates, {survivors} survive, {primeCount} primes, identity {identity:X16}.");
    }
    [Benchmark(OperationsPerInvoke = Count)]
    public ulong MillerRabin() => Run<Miller>();
    [Benchmark(Baseline = true, OperationsPerInvoke = Count)]
    public ulong BailliePsw() => Run<Baillie>();

    private ulong Run<T>() where T : struct, PrimeBenchmarkLoops.IDecision {
        var checksum = 0UL;

        foreach (var candidate in m_candidates) {
            var value = candidate.Value;
            var prime = (PassesFilter(value: value) && T.IsPrime(candidate: candidate));

            checksum = BitOperations.RotateLeft(offset: 3, value: checksum) ^ (prime ? value : ~value);
        }
        return checksum;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool PassesFilter(ulong value) {
        for (var i = 0; (i < m_inverses.Length); ++i) {
            if (unchecked((value * m_inverses[i])) <= m_ceilings[i]) { return false; }
        }
        return true;
    }

    private readonly struct Miller : PrimeBenchmarkLoops.IDecision {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsPrime(CandidateAddress candidate) => PrimeExploration.IsPrimeCandidate(candidate: candidate);
    }
    private readonly struct Baillie : PrimeBenchmarkLoops.IDecision {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsPrime(CandidateAddress candidate) => PrimeField64.IsBaillieProbablePrime(value: candidate.Value);
    }
}
