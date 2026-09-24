using System.Numerics;
using Xunit;

namespace Puck.Maths.Tests;

internal static partial class Subjects {
    public static string? CombinatoricsPoker() {
        Span<int> hand = stackalloc int[5];
        Span<int> decoded = stackalloc int[5];
        var rank = 0UL;

        for (hand[4] = 4; (hand[4] < 52); ++hand[4]) {
            for (hand[3] = 3; (hand[3] < hand[4]); ++hand[3]) {
                for (hand[2] = 2; (hand[2] < hand[3]); ++hand[2]) {
                    for (hand[1] = 1; (hand[1] < hand[2]); ++hand[1]) {
                        for (hand[0] = 0; (hand[0] < hand[1]); ++hand[0]) {
                            Assert.Equal(
                                rank,
                                Combinatorics.CombinationRank(
                                    combination: hand,
                                    n: 52
                                )
                            );
                            Combinatorics.CombinationUnrank(
                                destination: decoded,
                                n: 52,
                                rank: rank
                            );
                            Assert.True(condition: hand.SequenceEqual(other: decoded));
                            ++rank;
                        }
                    }
                }
            }
        }
        Assert.Equal(
            actual: rank,
            expected: 2_598_960UL
        );
        return null;
    }
    public static string? CombinatoricsCounts() {
        for (var n = 0; (n <= 128); ++n) {
            for (var k = 0; (k <= (n + 1)); ++k) {
                CheckCount(
                k: k,
                n: n
            );
            }
        }
        foreach (var n in new[] { 65535, 1_000_000, int.MaxValue }) {
            for (var k = 0; (k <= 8); ++k) {
                CheckCount(
                k: k,
                n: n
            ); CheckCount(
                k: (n - k),
                n: n
            );
            }
        }
        for (var n = 0; (n <= 21); ++n) {
            if (n <= 20) {
                Assert.Equal(
                ((ulong)Oracles.PermutationCount(n: n)),
                Combinatorics.Factorial(n: n)
            );
            } else { Assert.Throws<OverflowException>(testCode: () => Combinatorics.Factorial(n: n)); }
        }
        Assert.Equal(
            2_598_960UL,
            Combinatorics.Binomial(
                k: 5,
                n: 52
            )
        );
        return null;

        static void CheckCount(int n, int k) {
            var expected = Oracles.CombinatorialCount(
                k: k,
                n: n
            );

            if (expected > ulong.MaxValue) {
                Assert.Throws<OverflowException>(testCode: () => Combinatorics.Binomial(
                k: k,
                n: n
            ));
            } else {
                Assert.Equal(
                ((ulong)expected),
                Combinatorics.Binomial(
                    k: k,
                    n: n
                )
            );
            }
        }
    }
    public static string? CombinatoricsOrder() {
        var elements = new int[1000];
        var decoded = new int[1000];
        var ranks = new ulong[11];

        for (var n = 0; (n <= 10); ++n) {
            ranks.AsSpan().Clear();
            for (var mask = 0U; (mask < (1U << n)); ++mask) {
                var k = 0;

                for (var bit = 0; (bit < n); ++bit) { if ((mask & (1U << bit)) != 0) { elements[k++] = bit; } }
                var rank = ranks[k]++;

                Assert.Equal(
                    rank,
                    Combinatorics.CombinationRank(
                        n,
                        elements.AsSpan(
                            length: k,
                            start: 0
                        )
                    )
                );
                Combinatorics.CombinationUnrank(
                    n,
                    rank,
                    decoded.AsSpan(
                        length: k,
                        start: 0
                    )
                );
                Assert.True(condition: elements.AsSpan(
                    length: k,
                    start: 0
                ).SequenceEqual(other: decoded.AsSpan(
                    length: k,
                    start: 0
                )));
                for (var i = 0; (i < k); ++i) {
                    Assert.Equal(
                    elements[i],
                    Combinatorics.CombinationElement(
                        index: i,
                        k: k,
                        n: n,
                        rank: rank
                    )
                );
                }
            }
        }
        for (var n = 0; (n <= 8); ++n) {
            for (var i = 0; (i < n); ++i) { elements[i] = i; }
            var rank = 0UL;

            do {
                Assert.Equal(
                    rank,
                    Combinatorics.PermutationRank(permutation: elements.AsSpan(
                        length: n,
                        start: 0
                    ))
                );
                Combinatorics.PermutationUnrank(
                    rank,
                    decoded.AsSpan(
                        length: n,
                        start: 0
                    )
                );
                Assert.True(condition: elements.AsSpan(
                    length: n,
                    start: 0
                ).SequenceEqual(other: decoded.AsSpan(
                    length: n,
                    start: 0
                )));
                ++rank;
            } while (Oracles.NextLexicographicPermutation(elements: elements.AsSpan(
                length: n,
                start: 0
            )));
            Assert.Equal(
                ((ulong)Oracles.PermutationCount(n: n)),
                rank
            );
        }
        foreach (var (n, k) in new[] { (52, 5), (52, 7), (64, 32), (67, 33), (67, 34), (100, 99), (128, 2), (129, 2),
            (128, 127), (129, 128), (128, 128), (129, 129), (1000, 999), (1_000_000, 3), (int.MaxValue, 1), (int.MaxValue, 2) }) {
            var count = ((ulong)Oracles.CombinatorialCount(
                k: k,
                n: n
            ));

            foreach (var rank in BoundaryRanks(count: count)) {
                Combinatorics.CombinationUnrank(
                    n,
                    rank,
                    decoded.AsSpan(
                        length: k,
                        start: 0
                    )
                );
                Assert.Equal(
                    ((BigInteger)rank),
                    Oracles.ColexRank(elements: decoded.AsSpan(
                        length: k,
                        start: 0
                    ))
                );
                Assert.Equal(
                    rank,
                    Combinatorics.CombinationRank(
                        n,
                        decoded.AsSpan(
                            length: k,
                            start: 0
                        )
                    )
                );
                for (var i = 0; (i < k); ++i) {
                    Assert.InRange(
                        decoded[i],
                        ((i == 0)
                        ? 0
                        : (decoded[(i - 1)] + 1)),
                        (n - 1)
                    );
                    Assert.Equal(
                        decoded[i],
                        Combinatorics.CombinationElement(
                            index: i,
                            k: k,
                            n: n,
                            rank: rank
                        )
                    );
                }
            }
        }
        for (var n = 9; (n <= 20); ++n) {
            var count = ((ulong)Oracles.PermutationCount(n: n));

            foreach (var rank in BoundaryRanks(count: count)) {
                Combinatorics.PermutationUnrank(
                    rank,
                    decoded.AsSpan(
                        length: n,
                        start: 0
                    )
                );
                Assert.Equal(
                    ((BigInteger)rank),
                    Oracles.LexicographicRank(elements: decoded.AsSpan(
                        length: n,
                        start: 0
                    ))
                );
                Assert.Equal(
                    rank,
                    Combinatorics.PermutationRank(permutation: decoded.AsSpan(
                        length: n,
                        start: 0
                    ))
                );
                Assert.Equal(
                    n,
                    decoded.Take(count: n).Distinct().Count()
                );
                for (var i = 0; (i < n); ++i) {
                    Assert.InRange(
                    decoded[i],
                    0,
                    (n - 1)
                );
                }
            }
        }
        return null;
    }

    private static IEnumerable<ulong> BoundaryRanks(ulong count) {
        yield return 0;
        if (count == 1) { yield break; }
        yield return 1;
        yield return (count - 2);
        yield return (count - 1);
        for (var divisor = 2UL; (divisor <= 17); ++divisor) {
            yield return ((count / divisor) - 1);
            yield return (count / divisor);
            yield return ((count / divisor) + 1);
        }
    }

    public static string? CombinatoricsRefusals() {
        Assert.Throws<ArgumentOutOfRangeException>(
            paramName: "n",
            testCode: () => Combinatorics.Binomial(
                k: 0,
                n: -1
            )
        );
        Assert.Throws<ArgumentOutOfRangeException>(
            paramName: "k",
            testCode: () => Combinatorics.Binomial(
                k: -1,
                n: 3
            )
        );
        Assert.Throws<ArgumentOutOfRangeException>(
            paramName: "n",
            testCode: () => Combinatorics.Factorial(n: -1)
        );
        Assert.Throws<OverflowException>(testCode: () => Combinatorics.Factorial(n: int.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(
            paramName: "n",
            testCode: () => Combinatorics.CombinationRank(
                combination: [],
                n: -1
            )
        );
        Assert.Throws<ArgumentOutOfRangeException>(
            paramName: "combination",
            testCode: () => Combinatorics.CombinationRank(
                combination: new[] { 0, 1 },
                n: 1
            )
        );
        foreach (var invalid in new[] { new[] { -1, 1 }, new[] { 0, 3 }, new[] { 1, 1 }, new[] { 2, 1 } }) {
            Assert.Throws<ArgumentException>(
                paramName: "combination",
                testCode: () => Combinatorics.CombinationRank(
                    combination: invalid,
                    n: 3
                )
            );
            Assert.Throws<ArgumentException>(
                paramName: "permutation",
                testCode: () => Combinatorics.PermutationRank(permutation: invalid)
            );
        }
        Assert.Throws<ArgumentOutOfRangeException>(
            paramName: "permutation",
            testCode: () => Combinatorics.PermutationRank(permutation: new int[21])
        );
        Assert.Throws<OverflowException>(testCode: () => Combinatorics.CombinationRank(
            68,
            Enumerable.Range(
                count: 34,
                start: 0
            ).ToArray()
        ));
        Assert.Throws<ArgumentOutOfRangeException>(
            paramName: "n",
            testCode: () => Combinatorics.CombinationElement(
                index: 0,
                k: 0,
                n: -1,
                rank: 0
            )
        );
        Assert.Throws<ArgumentOutOfRangeException>(
            paramName: "k",
            testCode: () => Combinatorics.CombinationElement(
                index: 0,
                k: -1,
                n: 3,
                rank: 0
            )
        );
        Assert.Throws<ArgumentOutOfRangeException>(
            paramName: "k",
            testCode: () => Combinatorics.CombinationElement(
                index: 0,
                k: 4,
                n: 3,
                rank: 0
            )
        );
        Assert.Throws<ArgumentOutOfRangeException>(
            paramName: "index",
            testCode: () => Combinatorics.CombinationElement(
                index: -1,
                k: 2,
                n: 3,
                rank: 0
            )
        );
        Assert.Throws<ArgumentOutOfRangeException>(
            paramName: "index",
            testCode: () => Combinatorics.CombinationElement(
                index: 2,
                k: 2,
                n: 3,
                rank: 0
            )
        );
        Assert.Throws<ArgumentOutOfRangeException>(
            paramName: "index",
            testCode: () => Combinatorics.CombinationElement(
                index: 0,
                k: 0,
                n: 3,
                rank: 0
            )
        );
        Assert.Throws<ArgumentOutOfRangeException>(
            paramName: "rank",
            testCode: () => Combinatorics.CombinationElement(
                index: 0,
                k: 2,
                n: 3,
                rank: 3
            )
        );
        Assert.Throws<OverflowException>(testCode: () => Combinatorics.CombinationElement(
            index: 0,
            k: 34,
            n: 68,
            rank: 0
        ));
        var destination = Enumerable.Repeat(
            count: 34,
            element: -17
        ).ToArray();

        Assert.Throws<ArgumentOutOfRangeException>(
            paramName: "n",
            testCode: () => Combinatorics.CombinationUnrank(
                destination: destination,
                n: -1,
                rank: 0
            )
        );
        Assert.Throws<ArgumentOutOfRangeException>(
            paramName: "destination",
            testCode: () => Combinatorics.CombinationUnrank(
                destination: destination,
                n: 3,
                rank: 0
            )
        );
        Assert.Throws<ArgumentOutOfRangeException>(
            paramName: "rank",
            testCode: () => Combinatorics.CombinationUnrank(
                destination: destination,
                n: 34,
                rank: 1
            )
        );
        Assert.Throws<OverflowException>(testCode: () => Combinatorics.CombinationUnrank(
            destination: destination,
            n: 68,
            rank: 0
        ));
        Assert.All(
            destination,
            element => Assert.Equal(
                actual: element,
                expected: -17
            )
        );
        Assert.Throws<ArgumentOutOfRangeException>(
            paramName: "destination",
            testCode: () => Combinatorics.PermutationUnrank(
                destination: destination,
                rank: 0
            )
        );
        Assert.Throws<ArgumentOutOfRangeException>(
            paramName: "rank",
            testCode: () => Combinatorics.PermutationUnrank(
                6,
                destination.AsSpan(
                    length: 3,
                    start: 0
                )
            )
        );
        Assert.Throws<ArgumentOutOfRangeException>(
            paramName: "rank",
            testCode: () => Combinatorics.PermutationUnrank(
                ulong.MaxValue,
                destination.AsSpan(
                    length: 20,
                    start: 0
                )
            )
        );
        Assert.Throws<ArgumentOutOfRangeException>(
            paramName: "rank",
            testCode: () => Combinatorics.CombinationUnrank(
                3,
                ulong.MaxValue,
                destination.AsSpan(
                    length: 2,
                    start: 0
                )
            )
        );
        Assert.Throws<ArgumentOutOfRangeException>(
            paramName: "rank",
            testCode: () => Combinatorics.PermutationUnrank(
                1,
                Span<int>.Empty
            )
        );
        Assert.Throws<ArgumentOutOfRangeException>(
            paramName: "rank",
            testCode: () => Combinatorics.CombinationUnrank(
                0,
                1,
                Span<int>.Empty
            )
        );
        Assert.All(
            destination,
            element => Assert.Equal(
                actual: element,
                expected: -17
            )
        );
        return null;
    }
    /// <summary>Proves <see cref="LexicographicOrder.Compare{TPrimary, TSecondary}"/> decides by the primary key and
    /// falls to the secondary key only on a primary tie. Each draw is evaluated twice — as drawn, where the primary keys
    /// almost always differ, and with the right primary key forced equal to the left one — so both paths run on every
    /// draw rather than only where the sampler happens to land on a tie.</summary>
    /// <param name="left">The first record's primary and secondary keys.</param>
    /// <param name="right">The second record's primary and secondary keys.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? LexicographicOrderMatchesOracle(long[] left, long[] right) {
        for (var forcedTie = 0; (forcedTie < 2); ++forcedTie) {
            var rightPrimary = ((forcedTie == 0)
                ? right[0]
                : left[0]
            );
            var expected = (((BigInteger)left[0]) - rightPrimary).Sign;

            if (expected == 0) {
                expected = (((BigInteger)left[1]) - right[1]).Sign;
            }

            var actual = LexicographicOrder.Compare(
                leftPrimary: left[0],
                leftSecondary: left[1],
                rightPrimary: rightPrimary,
                rightSecondary: right[1]
            );

            if (actual != expected) {
                return $"({left[0]}, {left[1]}) against ({rightPrimary}, {right[1]}) compared {actual}, expected {expected}";
            }
        }

        return null;
    }
}
