using Puck.Maths;
using Xunit;

namespace Puck.World.Browser.Tests;

public sealed class BrowserCostReportLawTests {
    private static byte[] ExpensiveDocument() {
        var effects = Enumerable.Range(count: RuleCapacity.MaxEffectsPerRule, start: 0)
            .Select(selector: static _ => ((ActionEffect)new ActionEffect.AddState(State: "count", Value: 1m)))
            .ToArray();
        var definition = new WorldDefinition(
            StateRaw: new WorldStateSection(World: [
                new WorldStateRow(CellName.Parse(candidate: "items"), CellKind.Int, Capacity: StateCapacity.MaxCellsPerRow),
                new WorldStateRow(CellName.Parse(candidate: "count"), CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, CellValue.Int(value: 0))]),
            ]),
            Rules: [
                new WorldRule(CellName.Parse(candidate: "many-a"), effects, ForEach: StateChannelRef.OfName(name: "items")),
                new WorldRule(CellName.Parse(candidate: "many-b"), effects, ForEach: StateChannelRef.OfName(name: "items")),
            ]
        );

        return WorldDefinitionSerialization.Serialize(definition: definition);
    }

    [Fact]
    public void APreviewUsesTheSharedReportAndRebindReplacesIt() {
        var definition = new WorldDefinition();
        var session = new BrowserSession(definition: definition);
        var report = session.CostReport;

        Assert.Same(report, session.CostReport);
        var independent = WorldCostReport.Generate(definition);

        Assert.Equal(independent.TotalBound, report.TotalBound);
        Assert.Equal(independent.EvidenceDigest, report.EvidenceDigest);
        Assert.Equal(independent.WorkBudget, report.WorkBudget);
        var wire = BrowserCostReport.From(report: report);

        Assert.Equal(report.Resources.RowCount, wire.Resources.RowCount);
        Assert.Equal(report.Resources.JournalAllowanceBytes.ToString(provider: System.Globalization.CultureInfo.InvariantCulture), wire.Resources.JournalAllowanceBytes);
        Assert.Equal(report.Resources.UnmodeledTotalMemoryReason, wire.Resources.UnmodeledTotalMemoryReason);
        Assert.Equal(report.ContributorSources.Count, wire.ContributorSources.Count);

        Assert.True(condition: session.TryRebind(definition: definition with { Simulation = new WorldSimulationDefaults(RateHz: 30) }, reason: out var reason), userMessage: reason);
        Assert.NotSame(report, session.CostReport);
        Assert.Equal(30, session.CostReport.SimulationRateHz);
        Assert.Same(session.CostReport, session.CostReport);
    }
    [Fact]
    public void WireBoundsNeverInventAZeroForMissingEvidenceOrRoundAKnownCount() {
        Assert.Equal("9223372036854775807", BrowserCostBound.From(bound: CostBound.Known(cycles: long.MaxValue)).Cycles);
        Assert.Equal("0", BrowserCostBound.From(bound: CostBound.Zero).Cycles);
        var unresolved = BrowserCostBound.From(bound: CostBound.Unmodeled(reason: "no helper evidence"));

        Assert.Equal("Unmodeled", unresolved.Kind);
        Assert.Null(@object: unresolved.Cycles);
        Assert.Equal("no helper evidence", unresolved.Reason);
        Assert.Null(@object: BrowserCostBound.From(bound: CostBound.Overflow).Cycles);
    }
    [Fact]
    public void TooCostlyDraftRefusesInstallationButStillReturnsItsAnalysis() {
        var bytes = ExpensiveDocument();
        var validationErrors = new List<string>();
        var deferred = new List<string>();

        Assert.False(condition: BrowserParser.TryParseAndValidate(bytes, validationErrors, deferred, out _));
        Assert.Contains(collection: validationErrors, filter: static error => error.Contains(comparisonType: StringComparison.Ordinal, value: "the maximum of"));

        var analysis = BrowserCostAnalyzer.Analyze(utf8Json: bytes);

        Assert.True(condition: analysis.Ok, userMessage: string.Join(separator: Environment.NewLine, values: (analysis.Errors ?? [])));
        Assert.False(condition: analysis.Validated);
        Assert.NotNull(@object: analysis.Report);
        Assert.True(condition: (long.Parse(analysis.Report.HeuristicWorkUnitsPerTick, System.Globalization.CultureInfo.InvariantCulture) > RuleCapacity.MaxWorkUnitsPerTick));
        Assert.Contains(collection: analysis.ValidationErrors!, filter: static error => error.Message.Contains(comparisonType: StringComparison.Ordinal, value: "the maximum of"));
        Assert.Empty(collection: analysis.Deferred!);
        Assert.Null(@object: analysis.Errors);
    }
    [Fact]
    public void MalformedDraftRefusesAnalysisWithoutThrowing() {
        var analysis = BrowserCostAnalyzer.Analyze(utf8Json: "{ not-json"u8.ToArray());

        Assert.False(condition: analysis.Ok);
        Assert.Null(@object: analysis.Report);
        Assert.NotEmpty(collection: analysis.Errors!);
        Assert.Null(@object: analysis.ValidationErrors);
    }
    [Fact]
    public void ARuleWithAnInvalidReferenceCannotProduceAnAnalysisReport() {
        var definition = new WorldDefinition(Rules: [new WorldRule(
            CellName.Parse(candidate: "broken"),
            [new ActionEffect.AddState(State: "missing", Value: 1m)]
        )]);
        var analysis = BrowserCostAnalyzer.Analyze(utf8Json: WorldDefinitionSerialization.Serialize(definition: definition));

        Assert.False(condition: analysis.Ok);
        Assert.False(condition: analysis.Validated);
        Assert.Null(@object: analysis.Report);
        Assert.Contains(collection: analysis.Errors!, filter: static error => error.Message.Contains(comparisonType: StringComparison.Ordinal, value: "missing"));
    }
}
