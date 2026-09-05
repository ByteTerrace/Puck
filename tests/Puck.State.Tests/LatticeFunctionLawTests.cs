using Xunit;
using Puck.Maths;

namespace Puck.State.Tests;

/// <summary>The square family is <see cref="SquareIndex"/> exactly as the hex family is <see cref="HexagonalIndex"/>;
/// <c>gcd</c>/<c>lcm</c>, the floored <c>mod</c> with its two cycle spellings, and <c>smallestMissing</c> are the number-theory
/// calls a board author reaches for — each pinned against the Maths operation it names.</summary>
public sealed class LatticeFunctionLawTests {
    private static long Eval(string text) => ExpressionFunctionLawTests.EvalPublic(text: text);
    private static bool TryEval(string text) => ExpressionFunctionLawTests.TryEvalPublic(text: text);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(-3, 2)]
    [InlineData(4, 4)]
    [InlineData(-5, -1)]
    public void SquareIndicesRoundTripThroughTheGaussianBasis(int x, int y) {
        var index = Eval($"square({x}, {y})");
        var expected = SquareIndex.FromCoordinate(coordinate: new SquareCoordinate(X: x, Y: y));

        Assert.Equal(expected.Value, index);
        Assert.Equal(x, Eval($"squareX({index})"));
        Assert.Equal(y, Eval($"squareY({index})"));
        Assert.Equal(Math.Max(Math.Abs(x), Math.Abs(y)), Eval($"squareRadius({index})"));
        Assert.Equal(Math.Abs(x) + Math.Abs(y), Eval($"squareLength({index})"));
        Assert.Equal((x * x) + (y * y), Eval($"squareEuclideanSquared({index})"));
        Assert.Equal(Math.Abs(x) + Math.Abs(y), Eval($"squareDistance({index}, 0)"));
        Assert.Equal(Math.Max(Math.Abs(x), Math.Abs(y)), Eval($"squareChebyshev({index}, 0)"));
        Assert.Equal(index, Eval($"squareRotate({index}, 4)"));
        Assert.Equal(Eval($"square({-y}, {x})"), Eval($"squareRotate({index}, 1)"));
        Assert.Equal(index, Eval($"squareMirror(squareMirror({index}))"));
        Assert.Equal(Eval($"square({y}, {x})"), Eval($"squareSwap({index})"));
        Assert.Equal(Eval($"squareAdd({index}, square(2, -1))"), Eval($"squareTranslate({index}, 2, -1)"));
        Assert.Equal(index, Eval($"squareAdd(square(2, -1), squareSubtract({index}, square(2, -1)))"));
        Assert.Equal(index, Eval($"squareMultiply({index}, 1)"));
        Assert.Equal(Eval($"square({x * 3}, {y * 3})"), Eval($"squareScale({index}, 3)"));
    }

    [Fact]
    public void SquareNeighborsAreEastNorthWestSouth() {
        for (var direction = 0; direction < 4; direction++) {
            var unit = SquareCoordinate.Direction(direction: direction);

            Assert.Equal(SquareIndex.FromCoordinate(coordinate: unit).Value, Eval($"squareNeighbor(0, {direction})"));
            Assert.Equal(Eval($"squareNeighbor(0, {direction})"), Eval($"squareNeighbor(0, {direction + 4})"));
        }
        Assert.Equal(0L, Eval("squareNeighbor(squareNeighbor(0, 1), 3)"));
        Assert.False(TryEval("squareNeighbor(-1, 0)"));
        Assert.False(TryEval("squareX(9223372036854775807)"));
    }

    [Theory]
    [InlineData(12, 18, 6, 36)]
    [InlineData(-12, 18, 6, 36)]
    [InlineData(7, 0, 7, 0)]
    [InlineData(0, 0, 0, 0)]
    [InlineData(1, 1, 1, 1)]
    [InlineData(3, 5, 1, 15)]
    public void GcdAndLcmFollowTheMagnitudes(long a, long b, long gcd, long lcm) {
        Assert.Equal(gcd, Eval($"gcd({a}, {b})"));
        Assert.Equal(lcm, Eval($"lcm({a}, {b})"));
        Assert.Equal(a.GreatestCommonDivisor(other: b), Eval($"gcd({a}, {b})"));
    }

    [Fact]
    public void CoprimeStepsHaveNoInteriorLatticePoint() {
        Assert.Equal(1L, Eval("gcd(3, 5)"));
        Assert.Equal(3L, Eval("gcd(6, 9)")); // two interior points on the (6, 9) segment
        Assert.False(TryEval("lcm(4611686018427387904, 4611686018427387903)"));
        Assert.False(TryEval("gcd(-9223372036854775807 - 1, 2)"));
    }

    [Theory]
    [InlineData(-1, 14, 13)]
    [InlineData(15, 14, 1)]
    [InlineData(-15, 14, 13)]
    [InlineData(0, 14, 0)]
    [InlineData(7, -14, -7)]
    public void ModIsFloored(long a, long m, long expected) {
        Assert.Equal(expected, Eval($"mod({a}, {m})"));
        Assert.Equal(a.FloorModulo(modulus: m), Eval($"mod({a}, {m})"));
    }

    [Fact]
    public void CyclesWrapWithoutSign() {
        Assert.Equal(3L, Eval("cycleForward(12, 1, 14)"));   // 12 → 13 → 0 → 1
        Assert.Equal(11L, Eval("cycleForward(1, 12, 14)"));
        Assert.Equal(3L, Eval("cycleDistance(12, 1, 14)"));
        Assert.Equal(3L, Eval("cycleDistance(1, 12, 14)"));
        Assert.Equal(7L, Eval("cycleDistance(0, 7, 14)"));
        Assert.Equal(0L, Eval("cycleDistance(5, 5, 14)"));
        Assert.False(TryEval("mod(1, 0)"));
        Assert.False(TryEval("cycleForward(0, 1, 0)"));
        Assert.False(TryEval("cycleDistance(0, 1, -14)"));
    }

    [Theory]
    [InlineData(0L, 0L)]
    [InlineData(1L, 1L)]
    [InlineData(0b1011L, 2L)]
    [InlineData(0b0111L, 3L)]
    [InlineData(-1L, 64L)]
    public void MexIsTheSmallestClearBit(long mask, long expected) {
        Assert.Equal(expected, Eval($"smallestMissing({mask})"));
        Assert.Equal(Eval($"trailingZeroCount(~{mask})"), Eval($"smallestMissing({mask})"));
    }

    [Fact]
    public void SubsetsRankColexicographicallyAsBitmasks() {
        Assert.Equal(2598960L, Eval("choose(52, 5)"));
        Assert.Equal(1L, Eval("choose(7, 0)"));
        Assert.Equal(0L, Eval("choose(3, 5)"));
        Assert.Equal(2432902008176640000L, Eval("factorial(20)"));
        Assert.False(TryEval("factorial(21)"));
        Assert.False(TryEval("choose(-1, 0)"));
        Assert.False(TryEval("choose(70, 35)"));                       // exceeds 2^63

        // Every 3-subset of 0..7 round-trips through its rank; subsetMember reads the subset the mask spells.
        for (var rank = 0L; rank < 56L; rank++) {
            var mask = Eval($"subsetAt(8, 3, {rank})");
            Assert.Equal(3L, Eval($"popCount({mask})"));
            Assert.Equal(rank, Eval($"subsetRank(8, {mask})"));
            for (var index = 0; index < 3; index++) {
                var element = Eval($"subsetMember(8, 3, {rank}, {index})");
                Assert.NotEqual(0L, mask & (1L << ((int)element)));
                Assert.Equal(index, System.Numerics.BitOperations.PopCount(((ulong)mask) & ((1UL << ((int)element)) - 1UL)));
            }
        }
        Assert.Equal(0L, Eval("subsetRank(8, 7)"));                       // {0,1,2} is the first 3-subset
        Assert.Equal(55L, Eval("subsetRank(8, 224)"));                    // {5,6,7} is the last
        Assert.False(TryEval("subsetRank(8, 256)"));                      // bit 8 is outside 0..7
        Assert.False(TryEval("subsetAt(8, 3, 56)"));
        Assert.True(TryEval("subsetRank(64, -1)"));                       // the full word is a 64-subset of 0..63
        // A poker hand's identity: a 5-card mask over 52 ranks below choose(52, 5).
        Assert.True(Eval("subsetRank(52, (1 << 0) | (1 << 13) | (1 << 26) | (1 << 39) | (1 << 51))") < 2598960L);
    }

    [Fact]
    public void PermutationsRankLexicographicallyAsNibbles() {
        Assert.Equal(0L, Eval("arrangementRank(3, 0x210)"));                  // (0, 1, 2)
        Assert.Equal(5L, Eval("arrangementRank(3, 0x012)"));                  // (2, 1, 0)
        Assert.Equal(0x012L, Eval("arrangementAt(3, 5)"));
        Assert.Equal(2L, Eval("arrangementMember(3, 5, 0)"));
        Assert.False(TryEval("arrangementRank(3, 0x211)"));                   // a repeated element
        Assert.False(TryEval("arrangementAt(3, 6)"));
        Assert.False(TryEval("arrangementAt(17, 0)"));
        Assert.Equal(unchecked((long)0xFEDCBA9876543210UL), Eval("arrangementAt(16, 0)"));
        for (var rank = 0L; rank < 120L; rank++) {
            var packed = Eval($"arrangementAt(5, {rank})");
            Assert.Equal(rank, Eval($"arrangementRank(5, {packed})"));
            for (var position = 0; position < 5; position++) {
                Assert.Equal((packed >> (4 * position)) & 0xF, Eval($"arrangementMember(5, {rank}, {position})"));
            }
        }
    }

    [Fact]
    public void PrimesAreExactAndTheTableIsBounded() {
        long[] first = [2, 3, 5, 7, 11, 13, 17, 19, 23, 29];

        for (var index = 0; index < first.Length; index++) {
            Assert.Equal(first[index], Eval($"prime({index})"));
            Assert.Equal(1L, Eval($"isPrime({first[index]})"));
        }
        Assert.Equal(1619L, Eval("prime(255)"));
        Assert.False(TryEval("prime(256)"));
        Assert.False(TryEval("prime(-1)"));
        Assert.Equal(0L, Eval("isPrime(0)"));
        Assert.Equal(0L, Eval("isPrime(1)"));
        Assert.Equal(0L, Eval("isPrime(-7)"));
        Assert.Equal(0L, Eval("isPrime(1619 * 1621)"));
        Assert.Equal(1L, Eval("isPrime(4294967311)"));          // the first prime above 2^32 takes the 64-bit decider
        Assert.Equal(0L, Eval("isPrime(4294967297)"));          // 641 · 6700417
        Assert.Equal(1L, Eval("isPrime(9223372036854775783)")); // the largest prime below 2^63
        // A Gödel multiset: wood, stone, iron as exponents of prime(0..2); presence is divisibility.
        Assert.Equal(1L, Eval("(prime(0) * prime(0) * prime(2)) % prime(2) == 0"));
        Assert.Equal(0L, Eval("(prime(0) * prime(0) * prime(2)) % prime(1) == 0"));
    }

    [Fact]
    public void NimHeapsReduceThroughMexAndNimSum() {
        // A subtraction game {1, 2}: Grundy(n) = smallestMissing{Grundy(n-1), Grundy(n-2)} cycles 0,1,2 — spelled as an author would.
        Assert.Equal(0L, Eval("smallestMissing(0)"));
        Assert.Equal(1L, Eval("smallestMissing(1 << 0)"));
        Assert.Equal(2L, Eval("smallestMissing((1 << 0) | (1 << 1))"));
        Assert.Equal(0L, Eval("smallestMissing((1 << 1) | (1 << 2))"));
        Assert.Equal(0L, Eval("(3 ^ 5) ^ 6")); // three Nim heaps 3, 5, 6 — a losing position
    }
}
