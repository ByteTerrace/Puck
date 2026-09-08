using System.Numerics;
using Xunit;

namespace Puck.Maths.Tests;

internal static partial class Subjects {
    public static string? CombinatoricsPoker() {
        Span<int> hand = stackalloc int[5];
        Span<int> decoded = stackalloc int[5];
        var rank = 0UL;
        for (hand[4] = 4; hand[4] < 52; ++hand[4]) {
            for (hand[3] = 3; hand[3] < hand[4]; ++hand[3]) {
                for (hand[2] = 2; hand[2] < hand[3]; ++hand[2]) {
                    for (hand[1] = 1; hand[1] < hand[2]; ++hand[1]) {
                        for (hand[0] = 0; hand[0] < hand[1]; ++hand[0]) {
                            Assert.Equal(rank, Combinatorics.CombinationRank(52, hand));
                            Combinatorics.CombinationUnrank(52, rank, decoded);
                            Assert.True(hand.SequenceEqual(decoded));
                            ++rank;
                        }
                    }
                }
            }
        }
        Assert.Equal(2_598_960UL, rank);
        return null;
    }

    public static string? CombinatoricsCounts() {
        for (var n = 0; n <= 128; ++n) {
            for (var k = 0; k <= n + 1; ++k) { CheckCount(n, k); }
        }
        foreach (var n in new[] { 65535, 1_000_000, int.MaxValue }) {
            for (var k = 0; k <= 8; ++k) { CheckCount(n, k); CheckCount(n, n - k); }
        }
        for (var n = 0; n <= 21; ++n) {
            if (n <= 20) { Assert.Equal((ulong)Oracles.PermutationCount(n), Combinatorics.Factorial(n)); } else { Assert.Throws<OverflowException>(() => Combinatorics.Factorial(n)); }
        }
        Assert.Equal(2_598_960UL, Combinatorics.Binomial(52, 5));
        return null;

        static void CheckCount(int n, int k) {
            var expected = Oracles.CombinatorialCount(n, k);
            if (expected > ulong.MaxValue) { Assert.Throws<OverflowException>(() => Combinatorics.Binomial(n, k)); } else { Assert.Equal((ulong)expected, Combinatorics.Binomial(n, k)); }
        }
    }

    public static string? CombinatoricsOrder() {
        var elements = new int[1000];
        var decoded = new int[1000];
        var ranks = new ulong[11];
        for (var n = 0; n <= 10; ++n) {
            ranks.AsSpan().Clear();
            for (var mask = 0U; mask < (1U << n); ++mask) {
                var k = 0;
                for (var bit = 0; bit < n; ++bit) { if ((mask & (1U << bit)) != 0) { elements[k++] = bit; } }
                var rank = ranks[k]++;
                Assert.Equal(rank, Combinatorics.CombinationRank(n, elements.AsSpan(0, k)));
                Combinatorics.CombinationUnrank(n, rank, decoded.AsSpan(0, k));
                Assert.True(elements.AsSpan(0, k).SequenceEqual(decoded.AsSpan(0, k)));
                for (var i = 0; i < k; ++i) { Assert.Equal(elements[i], Combinatorics.CombinationElement(n, k, rank, i)); }
            }
        }
        for (var n = 0; n <= 8; ++n) {
            for (var i = 0; i < n; ++i) { elements[i] = i; }
            var rank = 0UL;
            do {
                Assert.Equal(rank, Combinatorics.PermutationRank(elements.AsSpan(0, n)));
                Combinatorics.PermutationUnrank(rank, decoded.AsSpan(0, n));
                Assert.True(elements.AsSpan(0, n).SequenceEqual(decoded.AsSpan(0, n)));
                ++rank;
            } while (Oracles.NextLexicographicPermutation(elements.AsSpan(0, n)));
            Assert.Equal((ulong)Oracles.PermutationCount(n), rank);
        }
        foreach (var (n, k) in new[] { (52, 5), (52, 7), (64, 32), (67, 33), (67, 34), (100, 99), (128, 2), (129, 2),
            (128, 127), (129, 128), (128, 128), (129, 129), (1000, 999), (1_000_000, 3), (int.MaxValue, 1), (int.MaxValue, 2) }) {
            var count = (ulong)Oracles.CombinatorialCount(n, k);
            foreach (var rank in BoundaryRanks(count)) {
                Combinatorics.CombinationUnrank(n, rank, decoded.AsSpan(0, k));
                Assert.Equal((BigInteger)rank, Oracles.ColexRank(decoded.AsSpan(0, k)));
                Assert.Equal(rank, Combinatorics.CombinationRank(n, decoded.AsSpan(0, k)));
                for (var i = 0; i < k; ++i) {
                    Assert.InRange(decoded[i], i == 0 ? 0 : decoded[i - 1] + 1, n - 1);
                    Assert.Equal(decoded[i], Combinatorics.CombinationElement(n, k, rank, i));
                }
            }
        }
        for (var n = 9; n <= 20; ++n) {
            var count = (ulong)Oracles.PermutationCount(n);
            foreach (var rank in BoundaryRanks(count)) {
                Combinatorics.PermutationUnrank(rank, decoded.AsSpan(0, n));
                Assert.Equal((BigInteger)rank, Oracles.LexicographicRank(decoded.AsSpan(0, n)));
                Assert.Equal(rank, Combinatorics.PermutationRank(decoded.AsSpan(0, n)));
                Assert.Equal(n, decoded.Take(n).Distinct().Count());
                for (var i = 0; i < n; ++i) { Assert.InRange(decoded[i], 0, n - 1); }
            }
        }
        return null;
    }

    private static IEnumerable<ulong> BoundaryRanks(ulong count) {
        yield return 0;
        if (count == 1) { yield break; }
        yield return 1;
        yield return count - 2;
        yield return count - 1;
        for (var divisor = 2UL; divisor <= 17; ++divisor) {
            yield return count / divisor - 1;
            yield return count / divisor;
            yield return count / divisor + 1;
        }
    }

    public static string? CombinatoricsRefusals() {
        Assert.Throws<ArgumentOutOfRangeException>("n", () => Combinatorics.Binomial(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>("k", () => Combinatorics.Binomial(3, -1));
        Assert.Throws<ArgumentOutOfRangeException>("n", () => Combinatorics.Factorial(-1));
        Assert.Throws<OverflowException>(() => Combinatorics.Factorial(int.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>("n", () => Combinatorics.CombinationRank(-1, []));
        Assert.Throws<ArgumentOutOfRangeException>("combination", () => Combinatorics.CombinationRank(1, new[] { 0, 1 }));
        foreach (var invalid in new[] { new[] { -1, 1 }, new[] { 0, 3 }, new[] { 1, 1 }, new[] { 2, 1 } }) {
            Assert.Throws<ArgumentException>("combination", () => Combinatorics.CombinationRank(3, invalid));
            Assert.Throws<ArgumentException>("permutation", () => Combinatorics.PermutationRank(invalid));
        }
        Assert.Throws<ArgumentOutOfRangeException>("permutation", () => Combinatorics.PermutationRank(new int[21]));
        Assert.Throws<OverflowException>(() => Combinatorics.CombinationRank(68, Enumerable.Range(0, 34).ToArray()));
        Assert.Throws<ArgumentOutOfRangeException>("n", () => Combinatorics.CombinationElement(-1, 0, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>("k", () => Combinatorics.CombinationElement(3, -1, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>("k", () => Combinatorics.CombinationElement(3, 4, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>("index", () => Combinatorics.CombinationElement(3, 2, 0, -1));
        Assert.Throws<ArgumentOutOfRangeException>("index", () => Combinatorics.CombinationElement(3, 2, 0, 2));
        Assert.Throws<ArgumentOutOfRangeException>("index", () => Combinatorics.CombinationElement(3, 0, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>("rank", () => Combinatorics.CombinationElement(3, 2, 3, 0));
        Assert.Throws<OverflowException>(() => Combinatorics.CombinationElement(68, 34, 0, 0));
        var destination = Enumerable.Repeat(-17, 34).ToArray();
        Assert.Throws<ArgumentOutOfRangeException>("n", () => Combinatorics.CombinationUnrank(-1, 0, destination));
        Assert.Throws<ArgumentOutOfRangeException>("destination", () => Combinatorics.CombinationUnrank(3, 0, destination));
        Assert.Throws<ArgumentOutOfRangeException>("rank", () => Combinatorics.CombinationUnrank(34, 1, destination));
        Assert.Throws<OverflowException>(() => Combinatorics.CombinationUnrank(68, 0, destination));
        Assert.All(destination, element => Assert.Equal(-17, element));
        Assert.Throws<ArgumentOutOfRangeException>("destination", () => Combinatorics.PermutationUnrank(0, destination));
        Assert.Throws<ArgumentOutOfRangeException>("rank", () => Combinatorics.PermutationUnrank(6, destination.AsSpan(0, 3)));
        Assert.Throws<ArgumentOutOfRangeException>("rank", () => Combinatorics.PermutationUnrank(ulong.MaxValue, destination.AsSpan(0, 20)));
        Assert.Throws<ArgumentOutOfRangeException>("rank", () => Combinatorics.CombinationUnrank(3, ulong.MaxValue, destination.AsSpan(0, 2)));
        Assert.Throws<ArgumentOutOfRangeException>("rank", () => Combinatorics.PermutationUnrank(1, Span<int>.Empty));
        Assert.Throws<ArgumentOutOfRangeException>("rank", () => Combinatorics.CombinationUnrank(0, 1, Span<int>.Empty));
        Assert.All(destination, element => Assert.Equal(-17, element));
        return null;
    }
}
