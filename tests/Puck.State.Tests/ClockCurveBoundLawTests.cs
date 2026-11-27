using System.Numerics;
using Puck.Maths;
using Xunit;

namespace Puck.State.Tests;

/// <summary>
/// CONTRACT UNDER TEST: <see cref="StateAdvance.TryValueCurve"/> bounds a closed-form curve's degree and coefficient
/// size before it expands them. A chain of nested shaped rates whose composed degree passes
/// <see cref="StateAdvance.MaxCurveCoefficients"/> answers no curve without composing it, and every chain that fits is
/// still produced exactly: the curve equals the exact clock value at each tick of its span.
/// </summary>
public sealed class ClockCurveBoundLawTests {
    private const ulong EngineTick = 1000UL;
    private const ulong ReadTick = (EngineTick * 7UL);

    private static long ReadValue(IStateClockCells cells, StateClockCell cell) => cell.Advance!.ComputeCurrentValue(
        baseValue: cell.Base,
        cells: cells,
        currentEngineTick: ReadTick,
        epochEngineTick: 0L,
        row: cell.Row
    );

    // A chain of Fixed rows: "r0" advances on the engine's seconds and "r<n>" runs on "r<n-1>" with one rate piece whose
    // polynomial has `rateCoefficients` small coefficients, so each link integrates to degree `rateCoefficients`.
    private static StateRowClockCells Chain(int links, int rateCoefficients) {
        var rows = new List<StateRow> {
            Row(name: "r0", advance: new StateAdvance(PerSecondNumerator: 1L, PerSecondDenominator: 1L)),
        };
        var coefficients = Enumerable.Range(start: 0, count: rateCoefficients).Select(index => $"1/{index + 2}").ToArray();

        for (var link = 1; (link <= links); link++) {
            rows.Add(Row(
                name: $"r{link}",
                advance: new StateAdvance(
                    PerSecondNumerator: 1L,
                    PerSecondDenominator: 1L,
                    By: $"r{link - 1}",
                    Rate: [new StateRatePiece(From: "0", Coefficients: coefficients)]
                )
            ));
        }

        return new StateRowClockCells(rows: rows);
    }
    private static StateRow Row(string name, StateAdvance advance) => new(
        Name: CellName.Parse(candidate: name),
        Kind: CellKind.Fixed,
        Advance: advance,
        Cells: [new StateCell(Key: StateRow.SlotKey, Value: CellValue.Fixed(rawBits: 16384L))]
    );
    private static bool TryCurve(StateRowClockCells cells, string row, out RationalPolynomial curve) =>
        StateAdvance.TryReadClockCurve(cells: cells, curve: out curve, depth: 0, engineTick: EngineTick, row: row);

    [Fact]
    public async Task ADeepChainOfShapedRates_AnswersNoCurve_WithoutComposingIt() {
        // Three shaped links of nine coefficients compose to degree 729; sixteen would compose to a degree past any
        // memory. The read must refuse each at the link whose composed degree passes the bound, so it returns.
        foreach (var links in new[] { 2, 3, 8, StateAdvance.MaxNestingDepth - 1 }) {
            var cells = Chain(links: links, rateCoefficients: (StateAdvance.MaxRateDegree + 1));
            var read = Task.Run(
                function: () => TryCurve(cells: cells, row: $"r{links}", curve: out _),
                cancellationToken: TestContext.Current.CancellationToken
            );

            Assert.False(condition: await read.WaitAsync(timeout: TimeSpan.FromSeconds(30), cancellationToken: TestContext.Current.CancellationToken), userMessage: $"a chain of {links} shaped links answered a curve");
            Assert.False(condition: TryCurve(cells: cells, row: $"r{links}", curve: out var refused));
            Assert.True(condition: refused.IsZero);
        }
    }
    [Fact]
    public void ACurveWhoseComposedDegreeFits_IsProducedExactly() {
        // Seven coefficients integrate to degree seven; two such links compose to degree 49 (50 coefficients), inside the
        // bound, and the curve is the exact value at each tick of the span.
        var cells = Chain(links: 2, rateCoefficients: 7);

        Assert.True(condition: TryCurve(cells: cells, row: "r2", curve: out var curve));
        Assert.Equal(expected: 49, actual: curve.Degree);
        Assert.True(condition: (curve.Coefficients.Length <= StateAdvance.MaxCurveCoefficients));

        foreach (var elapsed in new[] { 0UL, 1UL, 7UL, 400UL }) {
            Assert.True(condition: StateAdvance.TryReadExactClock(cells: cells, depth: 0, engineTick: (EngineTick + elapsed), row: "r2", value: out var exact));
            Assert.Equal(expected: exact, actual: curve.Evaluate(x: new Rational(Numerator: new BigInteger(value: elapsed), Denominator: BigInteger.One)));
        }
    }
    [Fact]
    public void ADegreeJustPastTheBound_IsRefused() {
        // Eight coefficients integrate to degree eight; two links compose to degree 64, one past the bound.
        Assert.False(condition: TryCurve(cells: Chain(links: 2, rateCoefficients: 8), row: "r2", curve: out _));
        Assert.True(condition: TryCurve(cells: Chain(links: 1, rateCoefficients: 8), row: "r1", curve: out _));
    }
    [Fact]
    public void ACoefficientPastTheBitBound_IsRefusedWhenTheShapeCompiles() {
        // A rate coefficient that spells more bits than the bound is refused by name where the shape compiles, so no read
        // ever evaluates it: the advance accumulates at its rate alone.
        var huge = (BigInteger.One << (StateAdvance.MaxCurveCoefficientBits + 8)).ToString();
        var pieces = new[] { new StateRatePiece(From: "0", Coefficients: [huge]) };

        Assert.False(condition: StateAdvance.TryCompileShape(pieces: pieces, reason: out var reason, shape: out var shape));
        Assert.Null(@object: shape);
        Assert.Contains(expectedSubstring: "rate[0].coefficients[0]", actualString: reason, comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: nameof(StateAdvance.MaxCurveCoefficientBits), actualString: reason, comparisonType: StringComparison.Ordinal);
        Assert.Null(@object: new StateAdvance(PerSecondNumerator: 1L, PerSecondDenominator: 1L, By: "r0", Rate: pieces).RateShape());
    }
    [Fact]
    public void APieceStartPastTheBitBound_IsRefusedWhenTheShapeCompiles() {
        var huge = (BigInteger.One << (StateAdvance.MaxCurveCoefficientBits + 8)).ToString();

        Assert.False(condition: StateAdvance.TryCompileShape(pieces: [new StateRatePiece(From: $"1/{huge}", Coefficients: ["1"])], reason: out var reason, shape: out _));
        Assert.Contains(expectedSubstring: "rate[0].from", actualString: reason, comparisonType: StringComparison.Ordinal);
    }
    [Fact]
    public void ACoefficientInsideTheBound_StillCompilesAtItsWidest() {
        var widest = (BigInteger.One << (StateAdvance.MaxCurveCoefficientBits - 1)).ToString();

        Assert.True(condition: StateAdvance.TryCompileShape(pieces: [new StateRatePiece(From: "0", Coefficients: [widest])], reason: out var reason, shape: out var shape), userMessage: reason);
        Assert.NotNull(@object: shape);
    }
    [Fact]
    public async Task TheExactRead_OfAChainOfShapedRates_ReturnsAndIsTheSameEachTime() {
        // Each shaped link multiplies the size of the exact value it reads by about nine, so a chain of sixteen is past
        // any memory unless the read bounds what it builds on. It answers, and answers the same value twice.
        foreach (var links in new[] { 2, 4, 8, StateAdvance.MaxNestingDepth }) {
            var cells = Chain(links: links, rateCoefficients: (StateAdvance.MaxRateDegree + 1));

            Assert.True(condition: cells.TryClockCell(row: $"r{links}", cell: out var cell));

            var read = Task.Run(
                function: () => (
                    First: ReadValue(cells: cells, cell: cell),
                    Second: ReadValue(cells: cells, cell: cell),
                    Turn: cell.Advance!.TurnAt(row: cell.Row, baseValue: cell.Base, cells: cells, currentEngineTick: ReadTick, epochEngineTick: 0L)
                ),
                cancellationToken: TestContext.Current.CancellationToken
            );
            var (first, second, _) = await read.WaitAsync(timeout: TimeSpan.FromSeconds(30), cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(expected: first, actual: second);
        }
    }
    [Fact]
    public void ThePastBoundParent_ReadsAsUnavailable_SoItsChildHoldsItsBase() {
        // Three shaped links of nine coefficients take the exact value past the bound, so the fourth reads a parent it
        // cannot build on and holds its base, while a chain inside the bound is read exactly.
        var cells = Chain(links: 4, rateCoefficients: (StateAdvance.MaxRateDegree + 1));

        Assert.True(condition: StateAdvance.TryReadExactClock(cells: cells, depth: 0, engineTick: ReadTick, row: "r3", value: out var third));
        Assert.False(condition: third.FitsBits(maxBits: StateAdvance.MaxExactValueBits));
        Assert.True(condition: cells.TryClockCell(row: "r4", cell: out var cell));
        Assert.Equal(expected: cell.Base, actual: ReadValue(cells: cells, cell: cell));

        var small = Chain(links: 2, rateCoefficients: 3);

        Assert.True(condition: small.TryClockCell(row: "r2", cell: out var near));
        Assert.NotEqual(expected: near.Base, actual: ReadValue(cells: small, cell: near));
    }
    // "r0" on the engine's seconds, "r1" on "r0" with one rate piece whose cubic term is `coefficient` (an integral of
    // degree three), and "r2" on "r1" with no shape. r1's curve coefficients carry its parent's turn through powers of the
    // tick rate, so its value at a later tick spells more bits than any one of them.
    private static StateRowClockCells Scaled(BigInteger coefficient) => new(rows: [
        Row(name: "r0", advance: new StateAdvance(PerSecondNumerator: 1L, PerSecondDenominator: 1L)),
        Row(name: "r1", advance: new StateAdvance(
            PerSecondNumerator: 1L,
            PerSecondDenominator: 1L,
            By: "r0",
            Rate: [new StateRatePiece(From: "0", Coefficients: ["0", "0", coefficient.ToString()])]
        )),
        Row(name: "r2", advance: new StateAdvance(PerSecondNumerator: 1L, PerSecondDenominator: 1L, By: "r1")),
    ]);

    [Fact]
    public void AParentPastTheExactBound_GivesNoCurve_AsTheExactReadHoldsItsChildAtItsBase() {
        // r1 has a curve of its own, yet its exact value spells past MaxExactValueBits at a later tick of that curve's span.
        // The authority's exact read treats it as unavailable there and r2 holds its base, so a curve predicting r2 from
        // r1's polynomial would disagree with it: r2 answers no curve.
        var cells = Scaled(coefficient: (BigInteger.One << 2030));
        var capped = (EngineTick + 30000UL);

        Assert.True(condition: TryCurve(cells: cells, row: "r1", curve: out _), userMessage: "the middle link builds on a small parent and has its curve");
        Assert.True(condition: StateAdvance.TryReadExactClock(cells: cells, depth: 0, engineTick: EngineTick, row: "r1", value: out var atAnchor));
        Assert.True(condition: atAnchor.FitsBits(maxBits: StateAdvance.MaxExactValueBits));
        Assert.True(condition: StateAdvance.TryReadExactClock(cells: cells, depth: 0, engineTick: capped, row: "r1", value: out var later));
        Assert.False(condition: later.FitsBits(maxBits: StateAdvance.MaxExactValueBits));
        Assert.False(condition: TryCurve(cells: cells, row: "r2", curve: out var refused));
        Assert.True(condition: refused.IsZero);
        Assert.True(condition: cells.TryClockCell(row: "r2", cell: out var cell));
        Assert.Equal(
            expected: cell.Base,
            actual: cell.Advance!.ComputeCurrentValue(baseValue: cell.Base, cells: cells, currentEngineTick: capped, epochEngineTick: 0L, row: cell.Row)
        );
    }
    [Fact]
    public void AParentThatStaysInsideTheExactBound_GivesACurveEqualToTheExactRead() {
        var cells = Scaled(coefficient: (BigInteger.One << 1000));

        Assert.True(condition: TryCurve(cells: cells, row: "r2", curve: out var curve));

        foreach (var elapsed in new[] { 0UL, 1UL, 9UL, 250UL, 6000UL, 30000UL }) {
            Assert.True(condition: StateAdvance.TryReadExactClock(cells: cells, depth: 0, engineTick: (EngineTick + elapsed), row: "r1", value: out var parent));
            Assert.True(condition: parent.FitsBits(maxBits: StateAdvance.MaxExactValueBits));
            Assert.True(condition: StateAdvance.TryReadExactClock(cells: cells, depth: 0, engineTick: (EngineTick + elapsed), row: "r2", value: out var exact));
            Assert.True(condition: TryCurve(cells: cells, row: "r1", curve: out var middle));
            Assert.True(condition: (middle.Evaluate(x: new Rational(Numerator: new BigInteger(value: elapsed), Denominator: BigInteger.One)) == parent), userMessage: $"r1 at +{elapsed}");
            Assert.True(condition: (exact == curve.Evaluate(x: new Rational(Numerator: new BigInteger(value: elapsed), Denominator: BigInteger.One))), userMessage: $"r2 at +{elapsed}");
        }
    }
    [Fact]
    public void APolynomialsValueBound_IsASufficientProofOfEveryWholeArgument() {
        var small = new RationalPolynomial(coefficients: [new Rational(Numerator: 1, Denominator: 3), new Rational(Numerator: 5, Denominator: 7)]);

        Assert.True(condition: small.ValuesFitBits(maxArgument: ulong.MaxValue, maxBits: 128));
        Assert.False(condition: small.ValuesFitBits(maxArgument: ulong.MaxValue, maxBits: 64));

        foreach (var argument in new BigInteger[] { 0, 1, 12345, ulong.MaxValue }) {
            Assert.True(condition: small.Evaluate(x: new Rational(Numerator: argument, Denominator: BigInteger.One)).FitsBits(maxBits: 128));
        }
    }
    [Fact]
    public void ARateTextOfAMegabyte_IsRefusedByLengthWithoutParsing() {
        // All zeros: parsed it is the value zero, which fits any bound, so a refusal can only be the length precheck.
        var zeros = new string(c: '0', count: (1 << 20));

        Assert.True(condition: Rational.TryParse(text: "000", value: out _));
        Assert.False(condition: Rational.TryParseBounded(text: zeros, maxBits: StateAdvance.MaxCurveCoefficientBits, value: out var value));
        Assert.True(condition: value.IsZero);
        Assert.False(condition: StateAdvance.TryCompileShape(pieces: [new StateRatePiece(From: "0", Coefficients: [zeros])], reason: out var reason, shape: out _));
        Assert.Contains(expectedSubstring: "rate[0].coefficients[0]", actualString: reason, comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: $"({zeros.Length} characters)", actualString: reason, comparisonType: StringComparison.Ordinal);
        Assert.True(condition: (reason.Length < 400), userMessage: "the refusal quotes a prefix, never the whole text");
        Assert.False(condition: StateAdvance.TryCompileShape(pieces: [new StateRatePiece(From: zeros, Coefficients: ["1"])], reason: out reason, shape: out _));
        Assert.True(condition: (reason.Length < 400));
    }
    [Fact]
    public void TheLongestBoundedText_IsWhatTheBitBoundCanSpell() {
        var longest = Rational.MaxTextLength(maxBits: StateAdvance.MaxCurveCoefficientBits);
        var widest = ((BigInteger.One << StateAdvance.MaxCurveCoefficientBits) - 1);
        var spelling = $"-{widest}/{widest}";

        // Each part of the widest quotient spells ceil(2048 * log10(2)) = 617 digits; the bound holds two such runs, a sign
        // and a slash, and the text of the widest quotient fits it.
        Assert.Equal(expected: 617, actual: widest.ToString().Length);
        Assert.Equal(expected: ((2 * 617) + 2), actual: longest);
        Assert.True(condition: (spelling.Length <= longest));
        Assert.True(condition: Rational.TryParseBounded(text: $"{widest}/1", maxBits: StateAdvance.MaxCurveCoefficientBits, value: out var parsed));
        Assert.Equal(expected: widest, actual: parsed.Numerator);
        Assert.False(condition: Rational.TryParseBounded(text: (BigInteger.One << StateAdvance.MaxCurveCoefficientBits).ToString(), maxBits: StateAdvance.MaxCurveCoefficientBits, value: out _));
    }
    [Fact]
    public void TheIntegralDegree_IsOneAboveTheHighestPiece() {
        Assert.Equal(expected: 1, actual: new StateAdvance(PerSecondNumerator: 1L, PerSecondDenominator: 1L).IntegralDegree());
        Assert.Equal(expected: 1, actual: new StateAdvance(PerSecondNumerator: 1L, PerSecondDenominator: 1L, By: "r0").IntegralDegree());
        Assert.Equal(
            expected: (StateAdvance.MaxRateDegree + 1),
            actual: new StateAdvance(
                PerSecondNumerator: 1L,
                PerSecondDenominator: 1L,
                By: "r0",
                Rate: [new StateRatePiece(From: "0", Coefficients: [.. Enumerable.Repeat(element: "1", count: (StateAdvance.MaxRateDegree + 1))])]
            ).IntegralDegree()
        );
    }
}
