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
        public string? BoundEachKey => null;
        public string? BoundPreviousKey { get; set; }
        public string? BoundTokenKey { get; set; }
        public StateCatalog Catalog { get; } = StateCatalog.Compile(section: null);
        public Span<long> PatternWord => [];
        public CompiledPatterns Patterns => CompiledPatterns.Empty;
        public StateStore Store => RowStore.Empty;
        public bool TableKeyMissing { get; set; }
        public ulong Tick => 0UL;

        public long BindingValue(int ordinal) => 0L;
        public Span<long> BoardScratch(int cells) => new long[cells];
        public int BoundIndex(BoundKey key) => -1;
        public void ReportTableKeyMissing(string table, long key) => TableKeyMissing = true;
        public CompiledTable Table(int ordinal) => throw new InvalidOperationException();
    }

    private static readonly RuleCompileContext Context = new(
        section: null,
        catalog: StateCatalog.Compile(section: null),
        tables: null,
        patterns: null,
        generators: null,
        simulationRateHz: 240,
        vocabulary: RuleVocabulary.Core
    );

    internal static long EvalPublic(string text) => Eval(text: text);
    internal static bool TryEvalPublic(string text) => TryEval(
        text: text,
        value: out _
    );

    private static CompiledExpressionToken[] Compile(string text, CellKind kind) {
        Assert.True(
            condition: ExpressionSpelling.TryParse(
                error: out var error,
                text: text,
                tokens: out var tokens
            ),
            userMessage: error
        );
        return RuleCompiler.CompileExpression(
            expression: new ValueExpression(Tokens: tokens),
            kind: kind,
            ruleName: "law",
            verb: "law",
            context: Context
        );
    }
    private static long Eval(string text, CellKind kind = CellKind.Int) {
        Assert.True(
            condition: TryEval(
                kind: kind,
                text: text,
                value: out var value
            ),
            userMessage: text
        );
        return value;
    }
    private static string Fixed(double value) => FixedQ4816.FromDouble(value: value).Value.ToString(provider: System.Globalization.CultureInfo.InvariantCulture);
    private static bool TryEval(string text, out long value, CellKind kind = CellKind.Int) =>
        RuleEvaluation.TryEvaluateExpression(
            reader: new StubReader(),
            program: Compile(
                kind: kind,
                text: text
            ),
            kind: kind,
            value: out value
        );

    [Fact]
    public void CenteredHexagonalLayersAreHexRings() {
        for (var index = 0L; (index < 400L); index++) {
            var radius = Eval($"hexRadius({index})");

            Assert.Equal(
                radius,
                Eval($"layer({index}, 6, 6, 1)")
            );
            Assert.Equal(
                ((radius == 0L)
                ? 0L
                : (1L + ((3L * radius) * (radius - 1L)))),
                Eval($"layerStart({radius}, 6, 6, 1)")
            );
            Assert.Equal(
                ((radius == 0L)
                ? 1L
                : (6L * radius)),
                Eval($"layerSize({radius}, 6, 6, 1)")
            );
            Assert.Equal(
                (index - Eval($"layerStart({radius}, 6, 6, 1)")),
                Eval($"layerOffset({index}, 6, 6, 1)")
            );
        }
        Assert.False(condition: TryEval(
            "layer(0, -1, 0, 0)",
            out _
        ));
        Assert.False(condition: TryEval(
            "layer(-1, 6, 6, 1)",
            out _
        ));
    }
    [InlineData("pair(a, b)")]
    [InlineData("replicationMask(width)")]
    [InlineData("repeatBits(pattern, width)")]
    [InlineData("hexNeighbor(hexIndex(q, r), 3)")]
    [InlineData("layerOffset(i, 6, 6, 1)")]
    [InlineData("hilbertIndex(3, x, y) + mortonX(m)")]
    [InlineData("squareRoot(a * a + b * b)")]
    [Theory]
    public void FunctionSpellingsPrintBackToThemselves(string text) {
        Assert.True(
            condition: ExpressionSpelling.TryParse(
                error: out var error,
                text: text,
                tokens: out var tokens
            ),
            userMessage: error
        );
        Assert.Equal(
            text,
            ExpressionSpelling.Print(tokens: tokens)
        );
    }
    [InlineData("hexNeighbor", typeof(ValueToken.HexNeighbor))]
    [InlineData("replicationMask", typeof(ValueToken.ReplicationMask))]
    [InlineData("repeatBits", typeof(ValueToken.RepeatBits))]
    [InlineData("pairSwap", typeof(ValueToken.PairSwap))]
    [InlineData("layerStart", typeof(ValueToken.LayerStart))]
    [InlineData("sqrt", typeof(ValueToken.SquareRoot))]
    [Theory]
    public void FunctionTokensCarryTheirCallNameAsDiscriminator(string discriminator, Type expected) {
        var token = JsonSerializer.Deserialize<ValueToken>(json: $"{{\"$type\":\"{discriminator}\"}}");

        Assert.IsType(
            expectedType: expected,
            @object: token
        );
    }
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(-2, 3)]
    [InlineData(4, 4)]
    [InlineData(-5, -1)]
    [Theory]
    public void HexIndicesRoundTripThroughTheEisensteinBasis(int q, int r) {
        var index = Eval($"hexIndex({q}, {r})");
        var expected = HexagonalIndex.FromCoordinate(coordinate: new HexagonalCoordinate(
            Q: q,
            R: r
        ));

        Assert.Equal(
            expected.Value,
            index
        );
        Assert.Equal(
            q,
            Eval($"hexQ({index})")
        );
        Assert.Equal(
            r,
            Eval($"hexR({index})")
        );
        Assert.Equal(
            expected.Radius,
            Eval($"hexRadius({index})")
        );
        Assert.Equal(
            expected.Norm,
            Eval($"hexEuclideanSquared({index})")
        );
        Assert.Equal(
            expected.Radius,
            Eval($"hexDistance({index}, 0)")
        );
        Assert.Equal(
            index,
            Eval($"hexRotate({index}, 6)")
        );
        Assert.Equal(
            index,
            Eval($"hexMirror(hexMirror({index}))")
        );
        Assert.Equal(
            index,
            Eval($"hexSwap(hexSwap({index}))")
        );
        Assert.Equal(
            Eval($"hexAdd({index}, hexIndex(2, -1))"),
            Eval($"hexTranslate({index}, 2, -1)")
        );
        Assert.Equal(
            index,
            Eval($"hexAdd(hexIndex(2, -1), hexSubtract({index}, hexIndex(2, -1)))")
        );
        Assert.Equal(
            index,
            Eval($"hexMultiply({index}, 1)")
        );
        Assert.Equal(
            Eval($"hexIndex({(q * 3)}, {(r * 3)})"),
            Eval($"hexScale({index}, 3)")
        );
    }
    [Fact]
    public void HexNeighborsCountTheSixDirectionsFromTheOrigin() {
        for (var direction = 0; (direction < 6); direction++) {
            Assert.Equal(
                (direction + 1),
                Eval($"hexNeighbor(0, {direction})")
            );
            Assert.Equal(
                (direction + 1),
                Eval($"hexNeighbor(0, {(direction + 6)})")
            );
            Assert.Equal(
                (direction + 1),
                Eval($"hexRotate(1, {direction})")
            );
        }
        Assert.Equal(
            0L,
            Eval("hexNeighbor(hexNeighbor(0, 2), 5)")
        );
        Assert.False(condition: TryEval(
            "hexNeighbor(-1, 0)",
            out _
        ));
        Assert.False(condition: TryEval(
            "hexQ(9223372036854775807)",
            out _
        ));
    }
    [InlineData(1, 0, 0)]
    [InlineData(1, 1, 1)]
    [InlineData(3, 5, 2)]
    [InlineData(8, 200, 17)]
    [InlineData(31, 2_147_483_647L, 0L)]
    [Theory]
    public void HilbertDistancesRoundTrip(int order, long x, long y) {
        var distance = Eval($"hilbertIndex({order}, {x}, {y})");

        Assert.Equal(
            ((long)HilbertCurve.Encode(
                order: order,
                x: ((uint)x),
                y: ((uint)y)
            )),
            distance
        );
        Assert.Equal(
            x,
            Eval($"hilbertX({order}, {distance})")
        );
        Assert.Equal(
            y,
            Eval($"hilbertY({order}, {distance})")
        );
    }
    [Fact]
    public void HilbertIsABijectionOnItsSquareAndRefusesOutsideIt() {
        var seen = new HashSet<long>();

        for (var x = 0; (x < 8); x++) {
            for (var y = 0; (y < 8); y++) {
                Assert.True(condition: seen.Add(item: Eval($"hilbertIndex(3, {x}, {y})")));
            }
        }
        Assert.Equal(
            64,
            seen.Count
        );
        Assert.False(condition: TryEval(
            "hilbertIndex(0, 0, 0)",
            out _
        ));
        Assert.False(condition: TryEval(
            "hilbertIndex(3, 8, 0)",
            out _
        ));
        Assert.False(condition: TryEval(
            "hilbertX(3, 64)",
            out _
        ));
    }
    [InlineData(0L, 0L)]
    [InlineData(1L, 0L)]
    [InlineData(0L, 1L)]
    [InlineData(1234L, 4321L)]
    [InlineData(2_147_483_647L, 2_147_483_647L)]
    [Theory]
    public void MortonCodesRoundTrip(long x, long y) {
        var code = Eval($"mortonIndex({x}, {y})");

        Assert.Equal(
            ((long)((uint)x).BitwisePair<uint, ulong>(other: ((uint)y))),
            code
        );
        Assert.Equal(
            x,
            Eval($"mortonX({code})")
        );
        Assert.Equal(
            y,
            Eval($"mortonY({code})")
        );
        Assert.False(condition: TryEval(
            "mortonIndex(2147483648, 0)",
            out _
        ));
    }
    [Fact]
    public void PairsRefuseOutsideTheSignedCell() {
        Assert.False(condition: TryEval(
            "pair(-1, 0)",
            out _
        ));
        Assert.False(condition: TryEval(
            "pair(3037000499, 0)",
            out _
        ));
        Assert.False(condition: TryEval(
            "pairX(-1)",
            out _
        ));
        Assert.False(condition: TryEval(
            "pairTranslate(pair(3037000498, 0), 1)",
            out _
        ));
        Assert.False(condition: TryEval(
            "pairScale(pair(2000000000, 0), 2)",
            out _
        ));
    }
    [InlineData(0L, 0L)]
    [InlineData(3L, 7L)]
    [InlineData(7L, 3L)]
    [InlineData(1L, 1L)]
    [InlineData(3_037_000_498L, 5L)]
    [InlineData(5L, 3_037_000_498L)]
    [Theory]
    public void PairsRoundTripAndAgreeWithTheAlgebra(long x, long y) {
        var pair = Eval($"pair({x}, {y})");

        Assert.Equal(
            ((long)((ulong)x).ElegantPair<ulong, ulong>(other: ((ulong)y))),
            pair
        );
        Assert.Equal(
            x,
            Eval($"pairX({pair})")
        );
        Assert.Equal(
            y,
            Eval($"pairY({pair})")
        );
        Assert.Equal(
            Eval($"pair({y}, {x})"),
            Eval($"pairSwap({pair})")
        );
        Assert.Equal(
            Math.Max(
                val1: x,
                val2: y
            ),
            Eval($"pairMaximum({pair})")
        );
        Assert.Equal(
            Math.Min(
                val1: x,
                val2: y
            ),
            Eval($"pairMinimum({pair})")
        );
        Assert.Equal(
            (x + y),
            Eval($"pairSum({pair})")
        );
        Assert.Equal(
            Math.Abs(value: (x - y)),
            Eval($"pairDifference({pair})")
        );
        if (Math.Max(
            val1: x,
            val2: y
        ) < 1_000_000L) {
            Assert.Equal(
                Eval($"pair({(x + 11)}, {(y + 11)})"),
                Eval($"pairTranslate({pair}, 11)")
            );
            Assert.Equal(
                Eval($"pair({(x * 3)}, {(y * 3)})"),
                Eval($"pairScale({pair}, 3)")
            );
        }
    }
    [Fact]
    public void RootsAndTrigonometryFollowTheirKinds() {
        Assert.Equal(
            4L,
            Eval("squareRoot(17)")
        );
        Assert.Equal(
            0L,
            Eval("squareRoot(0)")
        );
        Assert.False(condition: TryEval(
            "squareRoot(0 - 1)",
            out _
        ));
        Assert.Equal(
            FixedQ4816.FromInteger(value: 2L).Value,
            Eval(
                "squareRoot(4)",
                kind: CellKind.Fixed
            )
        );
        Assert.Equal(
            0L,
            Eval(
                "sine(0)",
                kind: CellKind.Fixed
            )
        );
        Assert.Equal(
            FixedQ4816.One.Value,
            Eval(
                "cosine(0)",
                kind: CellKind.Fixed
            )
        );
        Assert.Equal(
            FixedQ4816.Sin(angle: FixedQ4816.FromDouble(value: 1.5)).Value,
            Eval(
                "sine(1.5)",
                kind: CellKind.Fixed
            )
        );
        Assert.Throws<RuleException>(testCode: () => Compile(
            kind: CellKind.Int,
            text: "sine(1)"
        ));
        Assert.Throws<RuleException>(testCode: () => Compile(
            kind: CellKind.Fixed,
            text: "pair(1, 2)"
        ));
        Assert.Throws<RuleException>(testCode: () => Compile(
            kind: CellKind.Fixed,
            text: "replicationMask(8)"
        ));
        Assert.Throws<RuleException>(testCode: () => Compile(
            kind: CellKind.Fixed,
            text: "repeatBits(1, 8)"
        ));
        Assert.False(condition: ExpressionSpelling.TryParse(
            error: out _,
            text: "replicationMask(8, 8)",
            tokens: out _
        ));
        Assert.False(condition: ExpressionSpelling.TryParse(
            error: out _,
            text: "repeatBits(1)",
            tokens: out _
        ));
        Assert.False(condition: ExpressionSpelling.TryParse(
            error: out _,
            text: "hilbertIndex(1, 2)",
            tokens: out _
        ));
    }
}
