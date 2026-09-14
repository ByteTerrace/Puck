using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>CONTRACT UNDER TEST: <see cref="WorldDefinitionValidator"/>'s envelope refusals — <c>min</c>/<c>max</c>
/// and <c>overflow</c> are legal only on an Int/Fixed row; a one-sided range (a declared floor with no ceiling, or
/// the reverse) is legal on its own; both bounds present require <c>min &lt; max</c>; and a cell's value is checked
/// against whichever bound the row actually declares, independently, so an undeclared side never constrains it.</summary>
public sealed class WorldStateEnvelopeValidationLawTests {
    private static WorldDefinition BuildDefinition(params WorldStateRow[] rows) => new(
        Simulation: new WorldSimulationDefaults(RateHz: 240),
        StateRaw: new WorldStateSection(World: rows)
    );
    private static WorldStateRow Row(CellKind kind, long? min = null, long? max = null, StateOverflow overflow = StateOverflow.Refuse, long value = 0L) => new(
        Name: CellName.Parse(candidate: "gauge"),
        Kind: kind,
        Min: min,
        Max: max,
        Overflow: overflow,
        Cells: [new StateCell(
                Key: WorldStateRow.SlotKey,
                Value: value
            )]
    );
    // The validator's one refusal string (empty for a valid document).
    private static string Validate(WorldDefinition definition) =>
        (WorldDefinitionValidator.TryValidateLocally(
            definition: definition,
            reason: out var reason
        )
            ? string.Empty
            : reason
        );

    [Fact]
    public void ABoolRowDeclaringMinOrMaxIsRefusedByName() {
        var definition = BuildDefinition(Row(
            kind: CellKind.Bool,
            min: 0L
        ));

        Assert.Contains(
            actualString: Validate(definition: definition),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "only int/fixed rows carry a range"
        );
    }
    [Fact]
    public void ATextRowDeclaringOverflowIsRefusedByName() {
        var definition = BuildDefinition(Row(
            kind: CellKind.Text,
            overflow: StateOverflow.Saturate
        ) with { Cells = [new StateCell(Key: WorldStateRow.SlotKey, Text: "hi")] });

        Assert.Contains(
            actualString: Validate(definition: definition),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "only int/fixed rows carry an overflow policy"
        );
    }
    [Fact]
    public void BothBoundsPresentRequireMinStrictlyLessThanMax() {
        Assert.Contains(
            actualString: Validate(definition: BuildDefinition(Row(
                kind: CellKind.Int,
                max: 10L,
                min: 10L
            ))),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "must be less than max"
        );
        Assert.Contains(
            actualString: Validate(definition: BuildDefinition(Row(
                kind: CellKind.Int,
                max: 5L,
                min: 10L
            ))),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "must be less than max"
        );
        Assert.Equal(
            expected: string.Empty,
            actual: Validate(definition: BuildDefinition(Row(
                kind: CellKind.Int,
                max: 11L,
                min: 10L,
                value: 10L
            )))
        );
    }
    // A one-sided range validates on its own — the undeclared side constrains nothing.
    [InlineData(0L, null, long.MinValue, true)]
    [InlineData(0L, null, -1L, true)]
    [InlineData(null, 10L, long.MaxValue, true)]
    [InlineData(null, 10L, 11L, true)]
    [InlineData(null, 10L, long.MinValue, false)]
    [Theory]
    public void AOneSidedRangeChecksOnlyItsDeclaredSide(long? min, long? max, long value, bool expectRefusal) {
        var definition = BuildDefinition(Row(
            kind: CellKind.Int,
            max: max,
            min: min,
            value: value
        ));
        var reason = Validate(definition: definition);

        Assert.Equal(
            expected: expectRefusal,
            actual: (reason.Length > 0)
        );

        if (expectRefusal) {
            Assert.Contains(
                actualString: reason,
                comparisonType: StringComparison.Ordinal,
                expectedSubstring: ((min is not null)
                    ? "below its declared minimum"
                    : "above its declared maximum"
                )
            );
        }
    }
    [Fact]
    public void ARowDeclaringNeitherBoundAdmitsAnyCellValue() {
        var definition = BuildDefinition(Row(
            kind: CellKind.Int,
            value: long.MinValue
        ));

        Assert.Equal(
            expected: string.Empty,
            actual: Validate(definition: definition)
        );

        var atMax = BuildDefinition(Row(
            kind: CellKind.Int,
            value: long.MaxValue
        ));

        Assert.Equal(
            expected: string.Empty,
            actual: Validate(definition: atMax)
        );
    }
    [InlineData(StateOverflow.Refuse)]
    [InlineData(StateOverflow.Saturate)]
    [Theory]
    public void AFixedRowAdmitsBothOverflowPoliciesAlongsideARange(StateOverflow overflow) {
        var definition = BuildDefinition(Row(
            kind: CellKind.Fixed,
            max: 100L,
            min: 0L,
            overflow: overflow,
            value: 50L
        ));

        Assert.Equal(
            expected: string.Empty,
            actual: Validate(definition: definition)
        );
    }
}
