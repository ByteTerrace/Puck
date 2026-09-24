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
                        CellValue.Int(value: 0L)
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
    public void CompilationSharesOneReportAndRetainsItsEvidenceIdentity() {
        var definition = Document();
        var compilation = WorldRuleCompilation.Compile(definition: definition);
        var report = compilation.CostReport;

        Parallel.For(0, 16, _ => Assert.Same(report, compilation.CostReport));
        Assert.Same(compilation.WorkContributors, report.Contributors);
        Assert.Equal(compilation.WorkBudget, report.WorkBudget);
        Assert.Equal(WorldRuleWorkBudget.Measure(definition: definition), report.WorkBudget);
        Assert.Equal(CostModel.Default.EvidenceDigest, report.EvidenceDigest);
        Assert.False(condition: string.IsNullOrWhiteSpace(value: report.EvidenceDigest));
        Assert.True(condition: report.EditBurstBound.IsUnmodeled);
        Assert.Null(@object: report.Resources.MeasurementIssue);
        Assert.NotNull(value: report.Resources.ArenaFootprintBytes);
        Assert.Contains(
            expectedSubstring: "Total memory is unmodeled",
            actualString: report.Resources.UnmodeledTotalMemoryReason
        );

        var replacement = WorldRuleCompilation.Compile(definition: Document(rate: 30)).CostReport;

        Assert.NotSame(actual: replacement, expected: report);
        Assert.Equal(240, report.SimulationRateHz);
        Assert.Equal(30, replacement.SimulationRateHz);
        Assert.Equal(report.HeuristicWorkUnitsPerTick, replacement.HeuristicWorkUnitsPerTick);
        Assert.NotEqual(report.StepAllowanceCycles, replacement.StepAllowanceCycles);
    }
    [Fact]
    public void ResourceDimensionsMatchTheAdmittedLayoutWithoutCallingAllowancesAllocations() {
        var definition = Document();
        var report = WorldCostReport.Generate(definition: definition);
        var resources = report.Resources;
        var layout = ArenaLayout.Build(
            catalog: definition.StateCatalog,
            options: WorldSlotLanes.Options(definition: definition),
            section: definition.StateRaw
        );

        Assert.True(condition: StateArena.TryMeasureVisibility(
            bytes: out var visibilityBytes,
            reason: out var reason,
            section: definition.StateRaw
        ), userMessage: reason);
        Assert.Equal(expected: definition.StateCatalog.Count, actual: resources.RowCount);
        Assert.Equal(expected: 0, actual: resources.TopologyCount);
        Assert.Equal(expected: definition.Population.Capacity, actual: resources.PopulationCapacity);
        Assert.Equal(expected: layout.CellSlotCount, actual: resources.CellSlotCount);
        Assert.Equal(expected: layout.VectorByteCount, actual: resources.VectorComponentBytes);
        Assert.Equal(expected: layout.LaneSlotCount, actual: resources.LaneSlotCount);
        Assert.Equal(expected: layout.LaneRosterCount, actual: resources.LaneRosterCount);
        Assert.Equal(expected: layout.MaskWordCount, actual: resources.DrawMaskWordCount);
        Assert.Equal(expected: layout.Bytes, actual: resources.LayoutBytes);
        Assert.Equal(expected: visibilityBytes, actual: resources.RetainedVisibilityBytes);
        Assert.Equal(expected: definition.StateCatalog.Keys.Bytes, actual: resources.RetainedKeyBytes);
        Assert.Equal(
            expected: checked((checked((layout.Bytes + visibilityBytes)) + definition.StateCatalog.Keys.Bytes)),
            actual: resources.ArenaFootprintBytes
        );
        Assert.Equal(expected: ArenaCapacity.MaxBytes, actual: resources.ArenaAdmissionCeilingBytes);
        Assert.Equal(expected: ArenaCapacity.MaxJournalBytes, actual: resources.JournalAllowanceBytes);
        Assert.DoesNotContain(expectedSubstring: "allocated", actualString: resources.UnmodeledTotalMemoryReason);
    }
    [Fact]
    public void ResourceMeasurementRetainsVisibilityRefusalsInsteadOfReportingZeroBytes() {
        var definition = Document() with {
            StateRaw = new WorldStateSection(World: [new WorldStateRow(
                    Name: CellName.Parse(candidate: "count"),
                    Kind: CellKind.Int,
                    Cells: [new StateCell(
                            Key: WorldStateRow.SlotKey,
                            Value: CellValue.Int(value: 0L)
                        )],
                    Visibility: new StateVisibility(Readers: Enumerable.Repeat(
                        count: (StateCapacity.MaxVisibilityReaders + 1),
                        element: "reader"
                    ).ToArray())
                )]),
        };
        var resources = WorldResourceDimensions.Measure(definition: definition);

        Assert.Null(value: resources.ArenaFootprintBytes);
        Assert.Null(value: resources.RetainedVisibilityBytes);
        Assert.NotNull(@object: resources.MeasurementIssue);
        Assert.Contains(expectedSubstring: "reader limit", actualString: resources.MeasurementIssue);
    }
    [Fact]
    public void OverCeilingDraftRetainsItsExactArenaFootprintAndRefusal() {
        var readers = Enumerable.Range(count: StateCapacity.MaxVisibilityReaders, start: 0)
            .Select(selector: index => new string(c: ((char)('a' + (index % 26))), count: StateCapacity.MaxVisibilityReaderLength))
            .ToArray();
        var visibility = new StateVisibility(Readers: readers);
        var cells = new[] { "a", "b", "c", "d" }.Select(selector: key => new StateCell(
            Key: CellName.Parse(candidate: key),
            Value: CellValue.Int(value: 0L),
            Visibility: visibility
        )).ToArray();
        var rows = Enumerable.Range(count: 512, start: 0)
            .Select(selector: index => new WorldStateRow(
                Name: CellName.Parse(candidate: $"row{index}"),
                Kind: CellKind.Int,
                Capacity: cells.Length,
                Cells: cells
            ))
            .ToArray();
        var resources = WorldResourceDimensions.Measure(definition: new WorldDefinition(
            StateRaw: new WorldStateSection(World: rows)
        ));

        Assert.NotNull(value: resources.ArenaFootprintBytes);
        Assert.True(condition: (resources.ArenaFootprintBytes > resources.ArenaAdmissionCeilingBytes));
        Assert.NotNull(@object: resources.MeasurementIssue);
        Assert.Contains(expectedSubstring: "past the", actualString: resources.MeasurementIssue);
    }
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
        var definition = Document() with {
            SearchRaw = new WorldSearchSection(Jobs: [new WorldSearchRow(
                Name: "missing",
                Tokens: "absent"
            )]),
        };
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
