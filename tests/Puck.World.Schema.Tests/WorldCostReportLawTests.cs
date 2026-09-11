using System.Numerics;
using Xunit;

using Puck.Maths;

namespace Puck.World.Schema.Tests;

/// <summary>Portable policy arithmetic and honest reporting while calibration is incomplete.</summary>
public sealed class WorldCostReportLawTests {
    private static WorldDefinition Document(int rate = 240) => new(
        Simulation: new WorldSimulationDefaults(RateHz: rate),
        StateRaw: new WorldStateSection(World: [new WorldStateRow(CellName.Parse("count"), CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, 0L)])]),
        Rules: [new WorldRule(CellName.Parse("step"), [new ActionEffect.AddState(State: "count", Value: 1m)])]
    );

    [Theory]
    [InlineData(240, 210L, 6_250_000L)]
    [InlineData(30, 1_680L, 50_000_000L)]
    [InlineData(144, 350L, 10_416_666L)]
    public void PortablePolicyHasExactPeriodsAndAdmissionBoundaries(int rate, long period, long allowance) {
        var profile = CostModelProfile.Portable;
        Assert.Equal(period, profile.StepPeriodEngineTicks(rate));
        Assert.Equal(allowance, profile.AuthoredBudgetCycles(rate));
        Assert.True(profile.Admits(CostBound.Known(allowance), rate));
        Assert.False(profile.Admits(CostBound.Known(allowance + 1), rate));
        Assert.False(profile.Admits(CostBound.Overflow, rate));
        Assert.False(profile.Admits(CostBound.Unmodeled("missing kernel"), rate));
    }

    [Fact]
    public void ConversionsDoNotLosePrecisionOrSaturateBeforeDivision() {
        var profile = CostModelProfile.Portable;
        foreach (var cycles in new[] { 0L, 1L, 1_500_000L, 6_250_000L, long.MaxValue }) {
            var product = (BigInteger)cycles * 50_400;
            var quotient = BigInteger.DivRem(product, 3_000_000_000L, out var remainder);
            Assert.Equal((long)(quotient + (remainder.IsZero ? 0 : 1)), profile.ReferenceEngineTicks(cycles));
        }
        var precise = new CostModelProfile("test", 50_400, 1, 1);
        const long beyondDouble = 9_007_199_254_740_993L;
        Assert.Equal(beyondDouble, precise.ReferenceEngineTicks(beyondDouble));
        var largeShare = new CostModelProfile("test", long.MaxValue, long.MaxValue, long.MaxValue);
        Assert.Equal(long.MaxValue / 240, largeShare.AuthoredBudgetCycles(240));
        Assert.False(largeShare.Admits(CostBound.Known(long.MaxValue), 50_400));
        var fraction = profile.ReferenceStepFraction(long.MaxValue, 240);
        Assert.Equal((BigInteger)long.MaxValue * 240, (BigInteger)fraction.Numerator);
        Assert.Equal(3_000_000_000L, fraction.Denominator);
        Assert.Throws<OverflowException>(() => new CostModelProfile("test", 1, 1, 1).ReferenceEngineTicks(long.MaxValue));
    }

    [Fact]
    public void ZeroRateHasNoRecurringDeadlineAndInvalidPoliciesAreRefused() {
        var profile = CostModelProfile.Portable;
        Assert.Equal(0L, profile.AuthoredBudgetCycles(0));
        Assert.Equal(0L, profile.StepPeriodEngineTicks(0));
        Assert.False(profile.Admits(CostBound.Zero, 0));
        Assert.False(profile.Admits(CostBound.Overflow, 0));
        Assert.Throws<ArgumentException>(() => profile.StepPeriodEngineTicks(31));
        Assert.Throws<ArgumentException>(() => profile.AuthoredBudgetCycles(31));
        Assert.Throws<ArgumentOutOfRangeException>(() => profile.AuthoredBudgetCycles(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CostModelProfile("test", 0, 1, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CostModelProfile("test", 1, 2, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CostModelProfile("test", 1, 1, 0));
    }

    [Theory]
    [InlineData(240)]
    [InlineData(0)]
    public void HeuristicTotalsDoNotCertifyReferenceDeadlines(int rate) {
        var definition = Document(rate);
        var report = WorldCostReport.Generate(definition);
        Assert.Equal("puck.cost.portable.v1", report.ModelId);
        Assert.True(report.RecurringBound.IsUnmodeled);
        Assert.True(report.TotalBound.IsUnmodeled);
        Assert.Equal(CostBound.Zero, report.SearchReservations);
        Assert.Null(report.ReferenceEngineTicks);
        Assert.False(report.Admitted);
        Assert.Single(report.Contributors);
        Assert.NotEmpty(report.Issues);
        Assert.Equal(WorldRuleWorkBudget.Measure(definition).WorkUnitsPerTick, report.HeuristicWorkUnitsPerTick);
    }

    [Fact]
    public void FailedSearchPlanningIsUnmodeledRatherThanAFreeReservation() {
        var definition = Document() with { SearchRaw = new WorldSearchSection([new WorldSearchRow(Name: "missing", Tokens: "absent")]) };
        var report = WorldCostReport.Generate(definition);
        Assert.True(report.SearchReservations.IsUnmodeled);
        Assert.False(report.Admitted);
        Assert.Contains(report.Issues, issue => issue.StartsWith("Search planning issue:", StringComparison.Ordinal));
    }
}
