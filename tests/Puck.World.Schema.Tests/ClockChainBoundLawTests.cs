using System.Numerics;
using Puck.Maths;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>
/// CONTRACT UNDER TEST: a document's clock chain stays inside what the exact read affords, and an anchor a recipient
/// parses stays inside what a curve may carry. The validator refuses a chain whose exact value composes past
/// <see cref="StateAdvance.MaxExactDegree"/> and a rate coefficient past <see cref="StateAdvance.MaxCurveCoefficientBits"/>,
/// each by name; a chain inside both stays admitted, and an anchor whose coefficients are too wide for a curve is not
/// well formed.
/// </summary>
public sealed class ClockChainBoundLawTests {
    private static WorldStateRow Clock(string name, StateAdvance advance) => new(
        Name: CellName.Parse(candidate: name),
        Kind: CellKind.Fixed,
        Advance: advance,
        Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: CellValue.Fixed(rawBits: 16384L))]
    );
    // A chain "c0" on the engine's seconds, then "c<n>" nested on "c<n-1>" with one piece of `coefficients` coefficients,
    // or no shape at all for zero.
    private static WorldDefinition Chain(int links, int coefficients, string? coefficient = null) {
        StateRatePiece[]? pieces = ((coefficients == 0)
            ? null
            : [new StateRatePiece(From: "0", Coefficients: [.. Enumerable.Repeat(element: (coefficient ?? "1"), count: coefficients)])]
        );
        var rows = new List<WorldStateRow> { Clock(name: "c0", advance: new StateAdvance(PerSecondNumerator: 1L, PerSecondDenominator: 1L)) };

        for (var link = 1; (link <= links); link++) {
            rows.Add(Clock(name: $"c{link}", advance: new StateAdvance(PerSecondNumerator: 1L, PerSecondDenominator: 1L, By: $"c{link - 1}", Rate: pieces)));
        }

        return (Fixtures.BuildDocument() with { StateRaw = new WorldStateSection(World: rows) });
    }

    [Fact]
    public void AChainWhoseExactValueComposesPastTheBound_IsRefusedByName() {
        // Nine coefficients integrate to degree nine; three links compose to 729, two to 81, both past the bound.
        foreach (var links in new[] { 2, 3, StateAdvance.MaxNestingDepth }) {
            Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: Chain(links: links, coefficients: (StateAdvance.MaxRateDegree + 1)), neighbours: null, reason: out var reason));
            Assert.Contains(expectedSubstring: "composes to degree", actualString: reason, comparisonType: StringComparison.Ordinal);
            Assert.Contains(expectedSubstring: $"past the {StateAdvance.MaxExactDegree}", actualString: reason, comparisonType: StringComparison.Ordinal);
        }
    }
    [Fact]
    public void AChainInsideTheBound_IsAdmitted() {
        // Four coefficients integrate to degree four, so two such links compose to 16; one link of the widest shape is
        // degree nine; and sixteen unshaped links compose to degree one.
        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: Chain(links: 2, coefficients: 4), neighbours: null, reason: out var reason), userMessage: reason);
        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: Chain(links: 1, coefficients: (StateAdvance.MaxRateDegree + 1)), neighbours: null, reason: out reason), userMessage: reason);
        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: Chain(links: StateAdvance.MaxNestingDepth, coefficients: 0), neighbours: null, reason: out reason), userMessage: reason);
    }
    [Fact]
    public void ADegreeJustPastTheBound_IsRefused() {
        // Degree four per link composes to 4^2 = 16 and 4^3 = 64: the third link passes the bound of 24.
        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: Chain(links: 2, coefficients: 4), neighbours: null, reason: out var reason), userMessage: reason);
        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: Chain(links: 3, coefficients: 4), neighbours: null, reason: out reason));
        Assert.Contains(expectedSubstring: "composes to degree 64", actualString: reason, comparisonType: StringComparison.Ordinal);
    }
    [Fact]
    public void ARateCoefficientPastTheBitBound_IsRefusedByName() {
        var huge = (BigInteger.One << (StateAdvance.MaxCurveCoefficientBits + 8)).ToString();

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: Chain(links: 1, coefficients: 1, coefficient: huge), neighbours: null, reason: out var reason));
        Assert.Contains(expectedSubstring: nameof(StateAdvance.MaxCurveCoefficientBits), actualString: reason, comparisonType: StringComparison.Ordinal);
    }
    [Fact]
    public void AnAnchorWithACoefficientPastTheBitBound_IsNotWellFormed() {
        var curve = new RationalPolynomial(coefficients: [Rational.Zero, new Rational(Numerator: BigInteger.One, Denominator: 3)]);
        var anchor = new WorldClockAnchor(Curve: WorldClockAnchor.Spell(curve: curve), Phase: 0UL, Tick: 0UL);
        var huge = new WorldClockAnchor(
            Curve: ["0", (BigInteger.One << (WorldClockAnchor.MaxCurveCoefficientBits + 8)).ToString()],
            Phase: 0UL,
            Tick: 0UL
        );
        var widest = new WorldClockAnchor(
            Curve: ["0", (BigInteger.One << (WorldClockAnchor.MaxCurveCoefficientBits - 1)).ToString()],
            Phase: 0UL,
            Tick: 0UL
        );

        Assert.True(condition: anchor.IsWellFormed);
        Assert.False(condition: huge.IsWellFormed);
        Assert.True(condition: widest.IsWellFormed);
    }
    [Fact]
    public void AnAnchorCoefficientOfAMegabyte_IsRefusedByLengthWithoutParsing() {
        // All zeros: parsed it is the value zero, which fits any bound, so the anchor is malformed only because the text is
        // measured before it is read. Its prediction holds the anchor's own phase, as a malformed curve does.
        var zeros = new string(c: '0', count: (1 << 20));
        var anchor = new WorldClockAnchor(Curve: [zeros, "1"], Phase: 0UL, Tick: 0UL);

        Assert.False(condition: Rational.TryParseBounded(text: zeros, maxBits: WorldClockAnchor.MaxCurveCoefficientBits, value: out _));
        Assert.False(condition: anchor.IsWellFormed);
        Assert.Equal(expected: 0UL, actual: anchor.Predict(engineTick: 100UL));
    }
}
