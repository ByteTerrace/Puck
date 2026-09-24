using Xunit;
using Puck.Maths;

namespace Puck.State.Tests;

/// <summary>The square family is <see cref="SquareIndex"/> exactly as the hex family is <see cref="HexagonalIndex"/>;
/// <c>gcd</c>/<c>lcm</c>, the floored <c>mod</c> with its two cycle spellings, and <c>smallestMissing</c> are the number-theory
/// calls a board author reaches for — each pinned against the Maths operation it names.</summary>
public sealed class LatticeFunctionLawTests {
    private static long Eval(string text) => ExpressionFunctionLawTests.EvalPublic(text: text);
    private static bool TryEval(string text) => ExpressionFunctionLawTests.TryEvalPublic(text: text);

    [Fact]
    public void CoprimeStepsHaveNoInteriorLatticePoint() {
        Assert.Equal(
            1L,
            Eval(text: "greatestCommonDivisor(3, 5)")
        );
        Assert.Equal(
            3L,
            Eval(text: "greatestCommonDivisor(6, 9)")
        ); // two interior points on the (6, 9) segment
        Assert.False(condition: TryEval(text: "leastCommonMultiple(4611686018427387904, 4611686018427387903)"));
        Assert.False(condition: TryEval(text: "greatestCommonDivisor(-9223372036854775807 - 1, 2)"));
    }
    [Fact]
    public void CyclesWrapWithoutSign() {
        Assert.Equal(
            3L,
            Eval(text: "cycleForward(12, 1, 14)")
        );   // 12 → 13 → 0 → 1
        Assert.Equal(
            11L,
            Eval(text: "cycleForward(1, 12, 14)")
        );
        Assert.Equal(
            3L,
            Eval(text: "cycleDistance(12, 1, 14)")
        );
        Assert.Equal(
            3L,
            Eval(text: "cycleDistance(1, 12, 14)")
        );
        Assert.Equal(
            7L,
            Eval(text: "cycleDistance(0, 7, 14)")
        );
        Assert.Equal(
            0L,
            Eval(text: "cycleDistance(5, 5, 14)")
        );
        Assert.False(condition: TryEval(text: "floorModulo(1, 0)"));
        Assert.False(condition: TryEval(text: "cycleForward(0, 1, 0)"));
        Assert.False(condition: TryEval(text: "cycleDistance(0, 1, -14)"));
    }
    [InlineData(12, 18, 6, 36)]
    [InlineData(-12, 18, 6, 36)]
    [InlineData(7, 0, 7, 0)]
    [InlineData(0, 0, 0, 0)]
    [InlineData(1, 1, 1, 1)]
    [InlineData(3, 5, 1, 15)]
    [Theory]
    public void GcdAndLcmFollowTheMagnitudes(long a, long b, long gcd, long lcm) {
        Assert.Equal(
            gcd,
            Eval(text: $"greatestCommonDivisor({a}, {b})")
        );
        Assert.Equal(
            lcm,
            Eval(text: $"leastCommonMultiple({a}, {b})")
        );
        Assert.Equal(
            a.GreatestCommonDivisor(other: b),
            Eval(text: $"greatestCommonDivisor({a}, {b})")
        );
    }
    [InlineData(0L, 0L)]
    [InlineData(1L, 1L)]
    [InlineData(0b1011L, 2L)]
    [InlineData(0b0111L, 3L)]
    [InlineData(-1L, 64L)]
    [Theory]
    public void MexIsTheSmallestClearBit(long mask, long expected) {
        Assert.Equal(
            expected,
            Eval(text: $"smallestMissing({mask})")
        );
        Assert.Equal(
            Eval(text: $"trailingZeroCount(~{mask})"),
            Eval(text: $"smallestMissing({mask})")
        );
    }
    [InlineData(-1, 14, 13)]
    [InlineData(15, 14, 1)]
    [InlineData(-15, 14, 13)]
    [InlineData(0, 14, 0)]
    [InlineData(7, -14, -7)]
    [Theory]
    public void ModIsFloored(long a, long m, long expected) {
        Assert.Equal(
            expected,
            Eval(text: $"floorModulo({a}, {m})")
        );
        Assert.Equal(
            a.FloorModulo(modulus: m),
            Eval(text: $"floorModulo({a}, {m})")
        );
    }
    [Fact]
    public void NimHeapsReduceThroughMexAndNimSum() {
        // A subtraction game {1, 2}: Grundy(n) = smallestMissing{Grundy(n-1), Grundy(n-2)} cycles 0,1,2 — spelled as an author would.
        Assert.Equal(
            0L,
            Eval(text: "smallestMissing(0)")
        );
        Assert.Equal(
            1L,
            Eval(text: "smallestMissing(1 << 0)")
        );
        Assert.Equal(
            2L,
            Eval(text: "smallestMissing((1 << 0) | (1 << 1))")
        );
        Assert.Equal(
            0L,
            Eval(text: "smallestMissing((1 << 1) | (1 << 2))")
        );
        Assert.Equal(
            0L,
            Eval(text: "(3 ^ 5) ^ 6")
        ); // three Nim heaps 3, 5, 6 — a losing position
    }
    [Fact]
    public void PermutationsRankLexicographicallyAsNibbles() {
        Assert.Equal(
            0L,
            Eval(text: "arrangementRank(3, 0x210)")
        );                  // (0, 1, 2)
        Assert.Equal(
            5L,
            Eval(text: "arrangementRank(3, 0x012)")
        );                  // (2, 1, 0)
        Assert.Equal(
            0x012L,
            Eval(text: "arrangementAt(3, 5)")
        );
        Assert.Equal(
            2L,
            Eval(text: "arrangementMember(3, 5, 0)")
        );
        Assert.False(condition: TryEval(text: "arrangementRank(3, 0x211)"));                   // a repeated element
        Assert.False(condition: TryEval(text: "arrangementAt(3, 6)"));
        Assert.False(condition: TryEval(text: "arrangementAt(17, 0)"));
        Assert.Equal(
            unchecked((long)0xFEDCBA9876543210UL),
            Eval(text: "arrangementAt(16, 0)")
        );
        for (var rank = 0L; (rank < 120L); rank++) {
            var packed = Eval(text: $"arrangementAt(5, {rank})");

            Assert.Equal(
                rank,
                Eval(text: $"arrangementRank(5, {packed})")
            );
            for (var position = 0; (position < 5); position++) {
                Assert.Equal(
                    (packed >> (4 * position)) & 0xF,
                    Eval(text: $"arrangementMember(5, {rank}, {position})")
                );
            }
        }
    }
    [Fact]
    public void PrimesAreExactAndTheTableIsBounded() {
        long[] first = [2, 3, 5, 7, 11, 13, 17, 19, 23, 29];

        for (var index = 0; (index < first.Length); index++) {
            Assert.Equal(
                first[index],
                Eval(text: $"primeAt({index})")
            );
            Assert.Equal(
                1L,
                Eval(text: $"isPrime({first[index]})")
            );
        }
        Assert.Equal(
            1619L,
            Eval(text: "primeAt(255)")
        );
        Assert.False(condition: TryEval(text: "primeAt(256)"));
        Assert.False(condition: TryEval(text: "primeAt(-1)"));
        Assert.Equal(
            0L,
            Eval(text: "isPrime(0)")
        );
        Assert.Equal(
            0L,
            Eval(text: "isPrime(1)")
        );
        Assert.Equal(
            0L,
            Eval(text: "isPrime(-7)")
        );
        Assert.Equal(
            0L,
            Eval(text: "isPrime(1619 * 1621)")
        );
        Assert.Equal(
            1L,
            Eval(text: "isPrime(4294967311)")
        );          // the first prime above 2^32 takes the 64-bit decider
        Assert.Equal(
            0L,
            Eval(text: "isPrime(4294967297)")
        );          // 641 · 6700417
        Assert.Equal(
            1L,
            Eval(text: "isPrime(9223372036854775783)")
        ); // the largest prime below 2^63
        // A Gödel multiset: wood, stone, iron as exponents of primeAt(0..2); presence is divisibility.
        Assert.Equal(
            1L,
            Eval(text: "(primeAt(0) * primeAt(0) * primeAt(2)) % primeAt(2) == 0")
        );
        Assert.Equal(
            0L,
            Eval(text: "(primeAt(0) * primeAt(0) * primeAt(2)) % primeAt(1) == 0")
        );
    }
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(-3, 2)]
    [InlineData(4, 4)]
    [InlineData(-5, -1)]
    [Theory]
    public void SquareIndicesRoundTripThroughTheGaussianBasis(int x, int y) {
        var index = Eval(text: $"squareIndex({x}, {y})");
        var expected = SquareIndex.FromCoordinate(coordinate: new SquareCoordinate(
            X: x,
            Y: y
        ));

        Assert.Equal(
            expected.Value,
            index
        );
        Assert.Equal(
            x,
            Eval(text: $"squareX({index})")
        );
        Assert.Equal(
            y,
            Eval(text: $"squareY({index})")
        );
        Assert.Equal(
            Math.Max(
                val1: Math.Abs(value: x),
                val2: Math.Abs(value: y)
            ),
            Eval(text: $"squareRadius({index})")
        );
        Assert.Equal(
            (Math.Abs(value: x) + Math.Abs(value: y)),
            Eval(text: $"squareLength({index})")
        );
        Assert.Equal(
            ((x * x) + (y * y)),
            Eval(text: $"squareEuclideanSquared({index})")
        );
        Assert.Equal(
            (Math.Abs(value: x) + Math.Abs(value: y)),
            Eval(text: $"squareDistance({index}, 0)")
        );
        Assert.Equal(
            Math.Max(
                val1: Math.Abs(value: x),
                val2: Math.Abs(value: y)
            ),
            Eval(text: $"squareChebyshev({index}, 0)")
        );
        Assert.Equal(
            index,
            Eval(text: $"squareRotate({index}, 4)")
        );
        Assert.Equal(
            Eval(text: $"squareIndex({-y}, {x})"),
            Eval(text: $"squareRotate({index}, 1)")
        );
        Assert.Equal(
            index,
            Eval(text: $"squareMirror(squareMirror({index}))")
        );
        Assert.Equal(
            Eval(text: $"squareIndex({y}, {x})"),
            Eval(text: $"squareSwap({index})")
        );
        Assert.Equal(
            Eval(text: $"squareAdd({index}, squareIndex(2, -1))"),
            Eval(text: $"squareTranslate({index}, 2, -1)")
        );
        Assert.Equal(
            index,
            Eval(text: $"squareAdd(squareIndex(2, -1), squareSubtract({index}, squareIndex(2, -1)))")
        );
        Assert.Equal(
            index,
            Eval(text: $"squareMultiply({index}, 1)")
        );
        Assert.Equal(
            Eval(text: $"squareIndex({(x * 3)}, {(y * 3)})"),
            Eval(text: $"squareScale({index}, 3)")
        );
    }
    [Fact]
    public void SquareNeighborsAreEastNorthWestSouth() {
        for (var direction = 0; (direction < 4); direction++) {
            var unit = SquareCoordinate.Direction(direction: direction);

            Assert.Equal(
                SquareIndex.FromCoordinate(coordinate: unit).Value,
                Eval(text: $"squareNeighbor(0, {direction})")
            );
            Assert.Equal(
                Eval(text: $"squareNeighbor(0, {direction})"),
                Eval(text: $"squareNeighbor(0, {(direction + 4)})")
            );
        }
        Assert.Equal(
            0L,
            Eval(text: "squareNeighbor(squareNeighbor(0, 1), 3)")
        );
        Assert.False(condition: TryEval(text: "squareNeighbor(-1, 0)"));
        Assert.False(condition: TryEval(text: "squareX(9223372036854775807)"));
    }
    [Fact]
    public void SubsetsRankColexicographicallyAsBitmasks() {
        Assert.Equal(
            2598960L,
            Eval(text: "binomialCoefficient(52, 5)")
        );
        Assert.Equal(
            1L,
            Eval(text: "binomialCoefficient(7, 0)")
        );
        Assert.Equal(
            0L,
            Eval(text: "binomialCoefficient(3, 5)")
        );
        Assert.Equal(
            2432902008176640000L,
            Eval(text: "factorial(20)")
        );
        Assert.False(condition: TryEval(text: "factorial(21)"));
        Assert.False(condition: TryEval(text: "binomialCoefficient(-1, 0)"));
        Assert.False(condition: TryEval(text: "binomialCoefficient(70, 35)"));                       // exceeds 2^63

        // Every 3-subset of 0..7 round-trips through its rank; subsetMember reads the subset the mask spells.
        for (var rank = 0L; (rank < 56L); rank++) {
            var mask = Eval(text: $"subsetAt(8, 3, {rank})");

            Assert.Equal(
                3L,
                Eval(text: $"setBitCount({mask})")
            );
            Assert.Equal(
                rank,
                Eval(text: $"subsetRank(8, {mask})")
            );
            for (var index = 0; (index < 3); index++) {
                var element = Eval(text: $"subsetMember(8, 3, {rank}, {index})");

                Assert.NotEqual(
                    actual: mask & (1L << ((int)element)),
                    expected: 0L
                );
                Assert.Equal(
                    index,
                    System.Numerics.BitOperations.PopCount(value: ((ulong)mask) & ((1UL << ((int)element)) - 1UL))
                );
            }
        }
        // A member is read from the subset subsetAt decodes, so it shares that function's universe: a mask's 64.
        Assert.Equal(
            63L,
            Eval(text: "subsetMember(64, 64, 0, 63)")
        );
        Assert.False(condition: TryEval(text: "subsetMember(65, 1, 0, 0)"));
        Assert.False(condition: TryEval(text: "subsetAt(65, 1, 0)"));
        Assert.Equal(
            0L,
            Eval(text: "subsetRank(8, 7)")
        );                       // {0,1,2} is the first 3-subset
        Assert.Equal(
            55L,
            Eval(text: "subsetRank(8, 224)")
        );                    // {5,6,7} is the last
        Assert.False(condition: TryEval(text: "subsetRank(8, 256)"));                      // bit 8 is outside 0..7
        Assert.False(condition: TryEval(text: "subsetAt(8, 3, 56)"));
        Assert.True(condition: TryEval(text: "subsetRank(64, -1)"));                       // the full word is a 64-subset of 0..63
        // A poker hand's identity: a 5-card mask over 52 ranks below binomialCoefficient(52, 5).
        Assert.True(condition: (Eval(text: "subsetRank(52, (1 << 0) | (1 << 13) | (1 << 26) | (1 << 39) | (1 << 51))") < 2598960L));
    }
}
