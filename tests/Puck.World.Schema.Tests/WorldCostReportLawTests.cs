using System.Numerics;
using Xunit;

using Puck.Maths;

namespace Puck.World.Schema.Tests;

/// <summary>Portable policy arithmetic and honest reporting while calibration is incomplete.</summary>
public sealed class WorldCostReportLawTests {
    private static WorldDefinition Document(int rate = 240) => new(
        Simulation: new WorldSimulationDefaults(RateHz: rate),
        StateRaw: new WorldStateSection(World: [new WorldStateRow(
                CellName.Parse(candidate: "count"),
                CellKind.Int,
                Cells: [new StateCell(
                        WorldStateRow.SlotKey,
                        0L
                    )]
            )]),
        Rules: [new WorldRule(
                CellName.Parse(candidate: "step"),
                [new ActionEffect.AddState(
                        State: "count",
                        Value: 1m
                    )]
            )]
    );

    [Fact]
    public void ConversionsDoNotLosePrecisionOrSaturateBeforeDivision() {
        var profile = CostModelProfile.Portable;

        foreach (var cycles in new[] { 0L, 1L, 1_500_000L, 6_250_000L, long.MaxValue }) {
            var product = (((BigInteger)cycles) * 50_400);
            var quotient = BigInteger.DivRem(
                dividend: product,
                divisor: 3_000_000_000L,
                remainder: out var remainder
            );

            Assert.Equal(
                ((long)(quotient + (remainder.IsZero
                ? 0
                : 1))),
                profile.ReferenceEngineTicks(cycles: cycles)
            );
        }
        var precise = new CostModelProfile(
            cyclesPerSecond: 50_400,
            name: "test",
            subsystemShareDenominator: 1,
            subsystemShareNumerator: 1
        );
        const long BeyondDouble = 9_007_199_254_740_993L;

        Assert.Equal(
            BeyondDouble,
            precise.ReferenceEngineTicks(cycles: BeyondDouble)
        );
        var largeShare = new CostModelProfile(
            cyclesPerSecond: long.MaxValue,
            name: "test",
            subsystemShareDenominator: long.MaxValue,
            subsystemShareNumerator: long.MaxValue
        );

        Assert.Equal(
            (long.MaxValue / 240),
            largeShare.AuthoredBudgetCycles(rateHz: 240)
        );
        Assert.False(condition: largeShare.Admits(
            bound: CostBound.Known(cycles: long.MaxValue),
            rateHz: 50_400
        ));
        var fraction = profile.ReferenceStepFraction(
            cycles: long.MaxValue,
            rateHz: 240
        );

        Assert.Equal(
            actual: ((BigInteger)fraction.Numerator),
            expected: (((BigInteger)long.MaxValue) * 240)
        );
        Assert.Equal(
            actual: fraction.Denominator,
            expected: 3_000_000_000L
        );
        Assert.Throws<OverflowException>(testCode: () => new CostModelProfile(
            cyclesPerSecond: 1,
            name: "test",
            subsystemShareDenominator: 1,
            subsystemShareNumerator: 1
        ).ReferenceEngineTicks(cycles: long.MaxValue));
    }
    [Fact]
    public void FailedSearchPlanningIsUnmodeledRatherThanAFreeReservation() {
        var definition = Document() with { SearchRaw = new WorldSearchSection(Jobs: [new WorldSearchRow(
                Name: "missing",
                Tokens: "absent"
            )]) };
        var report = WorldCostReport.Generate(definition);

        Assert.True(condition: report.SearchReservations.IsUnmodeled);
        Assert.False(condition: report.Admitted);
        Assert.Contains(
            collection: report.Issues,
            filter: issue => issue.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: "Search planning issue:"
            )
        );
    }
    [InlineData(240)]
    [InlineData(0)]
    [Theory]
    public void HeuristicTotalsDoNotCertifyReferenceDeadlines(int rate) {
        var definition = Document(rate: rate);
        var report = WorldCostReport.Generate(definition);

        Assert.Equal(
            "puck.cost.portable-model.v1",
            report.ModelId
        );
        Assert.True(condition: report.RecurringBound.IsUnmodeled);
        Assert.True(condition: report.TotalBound.IsUnmodeled);
        Assert.Equal(
            CostBound.Zero,
            report.SearchReservations
        );
        Assert.Null(value: report.ReferenceEngineTicks);
        Assert.False(condition: report.Admitted);
        Assert.Single(collection: report.Contributors);
        Assert.NotEmpty(collection: report.Issues);
        Assert.Equal(
            WorldRuleWorkBudget.Measure(definition: definition).WorkUnitsPerTick,
            report.HeuristicWorkUnitsPerTick
        );
    }
    [InlineData(240, 210L, 6_250_000L)]
    [InlineData(30, 1_680L, 50_000_000L)]
    [InlineData(144, 350L, 10_416_666L)]
    [Theory]
    public void PortablePolicyHasExactPeriodsAndAdmissionBoundaries(int rate, long period, long allowance) {
        var profile = CostModelProfile.Portable;

        Assert.Equal(
            period,
            profile.StepPeriodEngineTicks(rateHz: rate)
        );
        Assert.Equal(
            allowance,
            profile.AuthoredBudgetCycles(rateHz: rate)
        );
        Assert.True(condition: profile.Admits(
            bound: CostBound.Known(cycles: allowance),
            rateHz: rate
        ));
        Assert.False(condition: profile.Admits(
            bound: CostBound.Known(cycles: (allowance + 1)),
            rateHz: rate
        ));
        Assert.False(condition: profile.Admits(
            bound: CostBound.Overflow,
            rateHz: rate
        ));
        Assert.False(condition: profile.Admits(
            bound: CostBound.Unmodeled(reason: "missing kernel"),
            rateHz: rate
        ));
    }
    [Fact]
    public void ZeroRateHasNoRecurringDeadlineAndInvalidPoliciesAreRefused() {
        var profile = CostModelProfile.Portable;

        Assert.Equal(
            0L,
            profile.AuthoredBudgetCycles(rateHz: 0)
        );
        Assert.Equal(
            0L,
            profile.StepPeriodEngineTicks(rateHz: 0)
        );
        Assert.False(condition: profile.Admits(
            bound: CostBound.Zero,
            rateHz: 0
        ));
        Assert.False(condition: profile.Admits(
            bound: CostBound.Overflow,
            rateHz: 0
        ));
        Assert.Throws<ArgumentException>(testCode: () => profile.StepPeriodEngineTicks(rateHz: 31));
        Assert.Throws<ArgumentException>(testCode: () => profile.AuthoredBudgetCycles(rateHz: 31));
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => profile.AuthoredBudgetCycles(rateHz: -1));
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new CostModelProfile(
            cyclesPerSecond: 0,
            name: "test",
            subsystemShareDenominator: 2,
            subsystemShareNumerator: 1
        ));
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new CostModelProfile(
            cyclesPerSecond: 1,
            name: "test",
            subsystemShareDenominator: 1,
            subsystemShareNumerator: 2
        ));
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new CostModelProfile(
            cyclesPerSecond: 1,
            name: "test",
            subsystemShareDenominator: 0,
            subsystemShareNumerator: 1
        ));
    }
}
