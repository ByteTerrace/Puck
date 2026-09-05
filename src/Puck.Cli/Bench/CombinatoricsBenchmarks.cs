using BenchmarkDotNet.Attributes;
using Puck.Maths;

namespace Puck.Cli.Bench;

[MemoryDiagnoser]
public class CombinationQueries {
    private const int Count = 512;
    private int m_n;
    private int m_k;
    private int[] m_elements = [];
    private ulong[] m_ranks = [];
    private int[] m_destination = [];

    [Params("Poker5", "Poker7", "Central67", "LargePairs")]
    public string Shape { get; set; } = "Poker5";

    [GlobalSetup]
    public void Setup() {
        (m_n, m_k) = Shape switch { "Poker5" => (52, 5), "Poker7" => (52, 7), "Central67" => (67, 33), "LargePairs" => (int.MaxValue, 2), _ => throw new InvalidOperationException() };
        m_elements = new int[Count * m_k];
        m_ranks = new ulong[Count];
        m_destination = new int[m_k];
        var random = new Random(Operands.Seed);
        var capacity = Combinatorics.Binomial(m_n, m_k);
        for (var i = 0; i < Count; ++i) {
            m_ranks[i] = (((ulong)random.NextInt64() << 1) | (uint)random.Next(2)) % capacity;
            var hand = m_elements.AsSpan(i * m_k, m_k);
            Combinatorics.CombinationUnrank(m_n, m_ranks[i], hand);
            if (Combinatorics.CombinationRank(m_n, hand) != m_ranks[i]) { throw new InvalidOperationException("Combination benchmark inputs disagree."); }
        }
    }

    [Benchmark(OperationsPerInvoke = Count)]
    public ulong Binomial() {
        var sink = 0UL;
        for (var i = 0; i < Count; ++i) { sink ^= Combinatorics.Binomial(m_elements[i * m_k + m_k - 1], m_k); }
        return sink;
    }

    [Benchmark(OperationsPerInvoke = Count)]
    public ulong Rank() {
        var sink = 0UL;
        for (var i = 0; i < Count; ++i) { sink ^= Combinatorics.CombinationRank(m_n, m_elements.AsSpan(i * m_k, m_k)); }
        return sink;
    }

    [Benchmark(OperationsPerInvoke = Count)]
    public int Unrank() {
        var sink = 0;
        for (var i = 0; i < Count; ++i) { Combinatorics.CombinationUnrank(m_n, m_ranks[i], m_destination); sink ^= m_destination[0]; }
        return sink;
    }

    [Benchmark(OperationsPerInvoke = Count)]
    public int LargestElement() {
        var sink = 0;
        for (var i = 0; i < Count; ++i) { sink ^= Combinatorics.CombinationElement(m_n, m_k, m_ranks[i], m_k - 1); }
        return sink;
    }
}

[MemoryDiagnoser]
public class PermutationQueries {
    private const int Count = 512;
    private int[] m_elements = [];
    private ulong[] m_ranks = [];
    private int[] m_destination = [];

    [Params(5, 10, 20)]
    public int Length { get; set; } = 5;

    [GlobalSetup]
    public void Setup() {
        m_elements = new int[Count * Length];
        m_ranks = new ulong[Count];
        m_destination = new int[Length];
        var random = new Random(Operands.Seed);
        for (var i = 0; i < Count; ++i) {
            m_ranks[i] = (ulong)random.NextInt64((long)Combinatorics.Factorial(Length));
            var permutation = m_elements.AsSpan(i * Length, Length);
            Combinatorics.PermutationUnrank(m_ranks[i], permutation);
            if (Combinatorics.PermutationRank(permutation) != m_ranks[i] || QuadraticRank(permutation) != m_ranks[i]) { throw new InvalidOperationException("Permutation benchmark inputs disagree."); }
        }
    }

    [Benchmark(OperationsPerInvoke = Count)]
    public ulong Rank() {
        var sink = 0UL;
        for (var i = 0; i < Count; ++i) { sink ^= Combinatorics.PermutationRank(m_elements.AsSpan(i * Length, Length)); }
        return sink;
    }

    [Benchmark(OperationsPerInvoke = Count)]
    public ulong RankQuadratic() {
        var sink = 0UL;
        for (var i = 0; i < Count; ++i) { sink ^= QuadraticRank(m_elements.AsSpan(i * Length, Length)); }
        return sink;
    }

    [Benchmark(OperationsPerInvoke = Count)]
    public int Unrank() {
        var sink = 0;
        for (var i = 0; i < Count; ++i) { Combinatorics.PermutationUnrank(m_ranks[i], m_destination); sink ^= m_destination[0]; }
        return sink;
    }

    // Independent inversion-count baseline, with the same length/range/duplicate validation.
    private static ulong QuadraticRank(ReadOnlySpan<int> permutation) {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(permutation.Length, 20, nameof(permutation));
        var result = 0UL;
        for (var i = 0; i < permutation.Length; ++i) {
            if ((uint)permutation[i] >= (uint)permutation.Length) { throw new ArgumentException("Invalid ordinal.", nameof(permutation)); }
            var smaller = 0U;
            for (var j = i + 1; j < permutation.Length; ++j) {
                if (permutation[j] == permutation[i]) { throw new ArgumentException("Duplicate ordinal.", nameof(permutation)); }
                if (permutation[j] < permutation[i]) { ++smaller; }
            }
            result = (result * (uint)(permutation.Length - i)) + smaller;
        }
        return result;
    }
}
