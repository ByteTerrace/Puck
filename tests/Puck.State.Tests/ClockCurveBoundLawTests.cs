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
