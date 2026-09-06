using System.Text.Json;
using Xunit;
using Puck.Maths;

namespace Puck.State.Tests;

/// <summary>The exotic function families an expression can spell — Szudzik pairs, Morton codes, Hilbert
/// distances, hex indices, layer sequences, and the square root and trigonometry — are exactly the Maths operations
/// they name: every inverse round-trips, every algebraic shortcut agrees with the long way, and a domain fault fails
/// the expression instead of wrapping.</summary>
public sealed class ExpressionFunctionLawTests {
    private sealed class StubReader : IRuleReader {
        public ulong Tick => 0UL;
        public StateStore Store => RowStore.Empty;
        public StateCatalog Catalog { get; } = StateCatalog.Compile(section: null);
        public CompiledPatterns Patterns => CompiledPatterns.Empty;
        public string? BoundEachKey => null;
        public string? BoundTokenKey { get; set; }
        public string? BoundPreviousKey { get; set; }
        public bool TableKeyMissing { get; set; }
        public Span<long> PatternWord => [];
        public int BoundIndex(BoundKey key) => -1;
        public long BindingValue(int ordinal) => 0L;
        public CompiledTable Table(int ordinal) => throw new InvalidOperationException();
        public void ReportTableKeyMissing(string table, long key) => TableKeyMissing = true;
        public Span<long> BoardScratch(int cells) => new long[cells];
    }

    private static readonly RuleCompileContext s_context = new(section: null, catalog: StateCatalog.Compile(section: null), tables: null, patterns: null, generators: null, simulationRateHz: 240, vocabulary: RuleVocabulary.Core);

    private static CompiledExpressionToken[] Compile(string text, CellKind kind) {
        Assert.True(ExpressionSpelling.TryParse(text: text, tokens: out var tokens, error: out var error), error);
        return RuleCompiler.CompileExpression(expression: new ValueExpression(Tokens: tokens), kind: kind, ruleName: "law", verb: "law", context: s_context);
    }

    private static bool TryEval(string text, out long value, CellKind kind = CellKind.Int) =>
        RuleEvaluation.TryEvaluateExpression(reader: new StubReader(), program: Compile(text: text, kind: kind), kind: kind, value: out value);

    private static long Eval(string text, CellKind kind = CellKind.Int) {
        Assert.True(TryEval(text: text, value: out var value, kind: kind), text);
        return value;
    }

    internal static long EvalPublic(string text) => Eval(text: text);
    internal static bool TryEvalPublic(string text) => TryEval(text: text, value: out _);

    private static string Fixed(double value) => FixedQ4816.FromDouble(value: value).Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    [Theory]
    [InlineData(0L, 0L)]
    [InlineData(3L, 7L)]
    [InlineData(7L, 3L)]
    [InlineData(1L, 1L)]
    [InlineData(3_037_000_498L, 5L)]
    [InlineData(5L, 3_037_000_498L)]
    public void PairsRoundTripAndAgreeWithTheAlgebra(long x, long y) {
        var pair = Eval($"pair({x}, {y})");

        Assert.Equal((long)((ulong)x).ElegantPair<ulong, ulong>(other: ((ulong)y)), pair);
        Assert.Equal(x, Eval($"pairX({pair})"));
        Assert.Equal(y, Eval($"pairY({pair})"));
        Assert.Equal(Eval($"pair({y}, {x})"), Eval($"pairSwap({pair})"));
        Assert.Equal(Math.Max(x, y), Eval($"pairMax({pair})"));
        Assert.Equal(Math.Min(x, y), Eval($"pairMin({pair})"));
        Assert.Equal(x + y, Eval($"pairSum({pair})"));
        Assert.Equal(Math.Abs(x - y), Eval($"pairDifference({pair})"));
        if (Math.Max(x, y) < 1_000_000L) {
            Assert.Equal(Eval($"pair({x + 11}, {y + 11})"), Eval($"pairTranslate({pair}, 11)"));
            Assert.Equal(Eval($"pair({x * 3}, {y * 3})"), Eval($"pairScale({pair}, 3)"));
        }
    }

    [Fact]
    public void PairsRefuseOutsideTheSignedCell() {
        Assert.False(TryEval("pair(-1, 0)", out _));
        Assert.False(TryEval("pair(3037000499, 0)", out _));
        Assert.False(TryEval("pairX(-1)", out _));
        Assert.False(TryEval("pairTranslate(pair(3037000498, 0), 1)", out _));
        Assert.False(TryEval("pairScale(pair(2000000000, 0), 2)", out _));
    }

    [Theory]
    [InlineData(0L, 0L)]
    [InlineData(1L, 0L)]
    [InlineData(0L, 1L)]
    [InlineData(1234L, 4321L)]
    [InlineData(2_147_483_647L, 2_147_483_647L)]
    public void MortonCodesRoundTrip(long x, long y) {
        var code = Eval($"morton({x}, {y})");

        Assert.Equal((long)((uint)x).BitwisePair<uint, ulong>(other: ((uint)y)), code);
        Assert.Equal(x, Eval($"mortonX({code})"));
        Assert.Equal(y, Eval($"mortonY({code})"));
        Assert.False(TryEval("morton(2147483648, 0)", out _));
    }

    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(1, 1, 1)]
    [InlineData(3, 5, 2)]
    [InlineData(8, 200, 17)]
    [InlineData(31, 2_147_483_647L, 0L)]
    public void HilbertDistancesRoundTrip(int order, long x, long y) {
        var distance = Eval($"hilbert({order}, {x}, {y})");

        Assert.Equal((long)HilbertCurve.Encode(order: order, x: ((uint)x), y: ((uint)y)), distance);
        Assert.Equal(x, Eval($"hilbertX({order}, {distance})"));
        Assert.Equal(y, Eval($"hilbertY({order}, {distance})"));
    }

    [Fact]
    public void HilbertIsABijectionOnItsSquareAndRefusesOutsideIt() {
        var seen = new HashSet<long>();

        for (var x = 0; x < 8; x++) {
            for (var y = 0; y < 8; y++) {
                Assert.True(seen.Add(item: Eval($"hilbert(3, {x}, {y})")));
            }
        }
        Assert.Equal(64, seen.Count);
        Assert.False(TryEval("hilbert(0, 0, 0)", out _));
        Assert.False(TryEval("hilbert(3, 8, 0)", out _));
        Assert.False(TryEval("hilbertX(3, 64)", out _));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(-2, 3)]
    [InlineData(4, 4)]
    [InlineData(-5, -1)]
    public void HexIndicesRoundTripThroughTheEisensteinBasis(int q, int r) {
        var index = Eval($"hex({q}, {r})");
        var expected = HexagonalIndex.FromCoordinate(coordinate: new HexagonalCoordinate(Q: q, R: r));

        Assert.Equal(expected.Value, index);
        Assert.Equal(q, Eval($"hexQ({index})"));
        Assert.Equal(r, Eval($"hexR({index})"));
        Assert.Equal(expected.Radius, Eval($"hexRadius({index})"));
        Assert.Equal(expected.Norm, Eval($"hexEuclideanSquared({index})"));
        Assert.Equal(expected.Radius, Eval($"hexDistance({index}, 0)"));
        Assert.Equal(index, Eval($"hexRotate({index}, 6)"));
        Assert.Equal(index, Eval($"hexMirror(hexMirror({index}))"));
        Assert.Equal(index, Eval($"hexSwap(hexSwap({index}))"));
        Assert.Equal(Eval($"hexAdd({index}, hex(2, -1))"), Eval($"hexTranslate({index}, 2, -1)"));
        Assert.Equal(index, Eval($"hexAdd(hex(2, -1), hexSubtract({index}, hex(2, -1)))"));
        Assert.Equal(index, Eval($"hexMultiply({index}, 1)"));
        Assert.Equal(Eval($"hex({q * 3}, {r * 3})"), Eval($"hexScale({index}, 3)"));
    }

    [Fact]
    public void HexNeighborsCountTheSixDirectionsFromTheOrigin() {
        for (var direction = 0; direction < 6; direction++) {
            Assert.Equal(direction + 1, Eval($"hexNeighbor(0, {direction})"));
            Assert.Equal(direction + 1, Eval($"hexNeighbor(0, {direction + 6})"));
            Assert.Equal(direction + 1, Eval($"hexRotate(1, {direction})"));
        }
        Assert.Equal(0L, Eval("hexNeighbor(hexNeighbor(0, 2), 5)"));
        Assert.False(TryEval("hexNeighbor(-1, 0)", out _));
        Assert.False(TryEval("hexQ(9223372036854775807)", out _));
    }

    [Fact]
    public void CenteredHexagonalLayersAreHexRings() {
        for (var index = 0L; index < 400L; index++) {
            var radius = Eval($"hexRadius({index})");

            Assert.Equal(radius, Eval($"layer({index}, 6, 6, 1)"));
            Assert.Equal((radius == 0L) ? 0L : (1L + (3L * radius * (radius - 1L))), Eval($"layerStart({radius}, 6, 6, 1)"));
            Assert.Equal((radius == 0L) ? 1L : (6L * radius), Eval($"layerSize({radius}, 6, 6, 1)"));
            Assert.Equal(index - Eval($"layerStart({radius}, 6, 6, 1)"), Eval($"layerOffset({index}, 6, 6, 1)"));
        }
        Assert.False(TryEval("layer(0, -1, 0, 0)", out _));
        Assert.False(TryEval("layer(-1, 6, 6, 1)", out _));
    }

    [Fact]
    public void RootsAndTrigonometryFollowTheirKinds() {
        Assert.Equal(4L, Eval("sqrt(17)"));
        Assert.Equal(0L, Eval("sqrt(0)"));
        Assert.False(TryEval("sqrt(0 - 1)", out _));
        Assert.Equal(FixedQ4816.FromInteger(value: 2L).Value, Eval("sqrt(4)", kind: CellKind.Fixed));
        Assert.Equal(0L, Eval("sin(0)", kind: CellKind.Fixed));
        Assert.Equal(FixedQ4816.One.Value, Eval("cos(0)", kind: CellKind.Fixed));
        Assert.Equal(FixedQ4816.Sin(angle: FixedQ4816.FromDouble(value: 1.5)).Value, Eval("sin(1.5)", kind: CellKind.Fixed));
        Assert.Throws<RuleException>(() => Compile(text: "sin(1)", kind: CellKind.Int));
        Assert.Throws<RuleException>(() => Compile(text: "pair(1, 2)", kind: CellKind.Fixed));
        Assert.Throws<RuleException>(() => Compile(text: "replicationMask(8)", kind: CellKind.Fixed));
        Assert.Throws<RuleException>(() => Compile(text: "repeatBits(1, 8)", kind: CellKind.Fixed));
        Assert.False(ExpressionSpelling.TryParse(text: "replicationMask(8, 8)", tokens: out _, error: out _));
        Assert.False(ExpressionSpelling.TryParse(text: "repeatBits(1)", tokens: out _, error: out _));
        Assert.False(ExpressionSpelling.TryParse(text: "hilbert(1, 2)", tokens: out _, error: out _));
    }

    [Theory]
    [InlineData("pair(a, b)")]
    [InlineData("replicationMask(width)")]
    [InlineData("repeatBits(pattern, width)")]
    [InlineData("hexNeighbor(hex(q, r), 3)")]
    [InlineData("layerOffset(i, 6, 6, 1)")]
    [InlineData("hilbert(3, x, y) + mortonX(m)")]
    [InlineData("sqrt(a * a + b * b)")]
    public void FunctionSpellingsPrintBackToThemselves(string text) {
        Assert.True(ExpressionSpelling.TryParse(text: text, tokens: out var tokens, error: out var error), error);
        Assert.Equal(text, ExpressionSpelling.Print(tokens: tokens));
    }

    [Theory]
    [InlineData("hexNeighbor", typeof(ValueToken.HexNeighbor))]
    [InlineData("replicationMask", typeof(ValueToken.ReplicationMask))]
    [InlineData("repeatBits", typeof(ValueToken.RepeatBits))]
    [InlineData("pairSwap", typeof(ValueToken.PairSwap))]
    [InlineData("layerStart", typeof(ValueToken.LayerStart))]
    [InlineData("sqrt", typeof(ValueToken.SquareRoot))]
    public void FunctionTokensCarryTheirCallNameAsDiscriminator(string discriminator, Type expected) {
        var token = JsonSerializer.Deserialize<ValueToken>(json: $"{{\"$type\":\"{discriminator}\"}}");

        Assert.IsType(expectedType: expected, @object: token);
    }
}
