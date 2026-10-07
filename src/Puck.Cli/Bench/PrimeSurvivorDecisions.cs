using System.Numerics;
using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using Puck.Maths;

namespace Puck.Cli.Bench;

// A shared candidate stream and checksum isolate production Baillie–PSW and the historical Miller–Rabin control.
// Candidate construction, bounded trial division, and independent result checks stay outside timing.
[MemoryDiagnoser]
public class PrimeSurvivorDecisions {
    private const int Count = 256;

    private CandidateAddress[] m_candidates = [];

    [Params("1e12", "1e18", "U64Top")]
    public string Band { get; set; } = "1e12";
    [Params("Presieved", "Primes")]
    public string Workload { get; set; } = "Presieved";

    [GlobalSetup]
    public void Setup() {
        var low = Band switch {
            "1e12" => 1_000_000_000_000UL,
            "1e18" => 1_000_000_000_000_000_000UL,
            "U64Top" => (ulong.MaxValue - 1_000_000UL),
            _ => throw new InvalidOperationException(message: "Unknown primality band."),
        };

        if (Workload is not ("Presieved" or "Primes")) { throw new InvalidOperationException(message: "Unknown primality workload."); }
        var bases = new List<ulong>();

        PrimeExploration.Enumerate(7, 65535, bases.Add);
        m_candidates = new CandidateAddress[Count];
        var filled = 0;
        var primeCount = 0;
        var identity = 0UL;

        for (var value = low; (filled < Count); ++value) {
            if (value == ulong.MaxValue) { throw new InvalidOperationException(message: "Candidate band exhausted."); }
            if (!CandidateAddress.TryFromValue(address: out var candidate, value: value)) { continue; }
            var survives = true;

            foreach (var prime in bases) {
                if ((value % prime) == 0) { survives = false; break; }
            }
            if (!survives) { continue; }
            var expected = PrimeExplorationBenchmarkReference.IsPrimeCandidate(value: value);

            if ((Workload == "Primes") && !expected) { continue; }
            if ((PrimeMillerRabinBaseline.IsPrimeCandidate(value: value) != expected) ||
                (PrimeField64.IsBaillieProbablePrime(value: value) != expected)) {
                throw new InvalidOperationException(message: $"Primality alternatives disagree at {value}.");
            }
            m_candidates[filled++] = candidate;
            if (expected) { ++primeCount; }
            identity = BitOperations.RotateLeft(offset: 3, value: identity) ^ value;
        }
        if ((MillerRabin() != UInt128Reference()) || (BailliePsw() != UInt128Reference())) {
            throw new InvalidOperationException(message: "Primality checksums disagree.");
        }
        Console.WriteLine(value: $"prime decisions: {Band}/{Workload}; {Count} candidates; {primeCount} primes; identity {identity:X16}.");
    }
    [Benchmark(Baseline = true, OperationsPerInvoke = Count)]
    public ulong MillerRabin() => PrimeBenchmarkLoops.Decisions<Montgomery>(candidates: m_candidates, trialDivisors: 0);
    [Benchmark(OperationsPerInvoke = Count)]
    public ulong BailliePsw() => PrimeBenchmarkLoops.Decisions<Baillie>(candidates: m_candidates, trialDivisors: 0);
    [Benchmark(OperationsPerInvoke = Count)]
    public ulong UInt128Reference() => PrimeBenchmarkLoops.Decisions<Division>(candidates: m_candidates, trialDivisors: 0);

    private readonly struct Montgomery : PrimeBenchmarkLoops.IDecision {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsPrime(CandidateAddress candidate) => PrimeMillerRabinBaseline.IsPrimeCandidate(value: candidate.Value);
    }
    private readonly struct Baillie : PrimeBenchmarkLoops.IDecision {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsPrime(CandidateAddress candidate) => PrimeField64.IsBaillieProbablePrime(value: candidate.Value);
    }
    private readonly struct Division : PrimeBenchmarkLoops.IDecision {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsPrime(CandidateAddress candidate) => PrimeExplorationBenchmarkReference.IsPrimeCandidate(value: candidate.Value);
    }
}
