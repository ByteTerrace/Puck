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
    public void ACoefficientPastTheBitBound_IsRefusedAtLowDegree() {
        // A rate whose own coefficient spells more bits than the bound makes a curve of degree one that is still refused.
        var huge = (BigInteger.One << (StateAdvance.MaxCurveCoefficientBits + 8)).ToString();
        var rows = new List<StateRow> {
            Row(name: "r0", advance: new StateAdvance(PerSecondNumerator: 1L, PerSecondDenominator: 1L)),
            Row(name: "r1", advance: new StateAdvance(
                PerSecondNumerator: 1L,
                PerSecondDenominator: 1L,
                By: "r0",
                Rate: [new StateRatePiece(From: "0", Coefficients: [huge])]
            )),
        };

        Assert.False(condition: TryCurve(cells: new StateRowClockCells(rows: rows), row: "r1", curve: out var curve));
        Assert.True(condition: curve.IsZero);
    }
}
