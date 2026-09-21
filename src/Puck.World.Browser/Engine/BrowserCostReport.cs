using System.Globalization;
using Puck.Maths;

namespace Puck.World.Browser.Engine;

/// <summary>A reference-cycle bound on the browser wire. An unresolved bound has no numeric payload; a known
/// 64-bit count is a decimal string, preserving values beyond JavaScript's exact integer range.</summary>
public sealed record BrowserCostBound(string Kind, string? Cycles, string? Reason) {
    /// <summary>Preserves the bound's status and exact count.</summary>
    public static BrowserCostBound From(CostBound bound) => new(
        bound.Kind.ToString(),
        (bound.IsKnown ? bound.Cycles.ToString(provider: CultureInfo.InvariantCulture) : null),
        bound.Reason
    );
}
/// <summary>One heuristic contributor. Every work value remains explicitly separate from reference cycles.</summary>
public sealed record BrowserCostContributor(string Name, bool IsInteraction, string Multiplier, string Setup, string Check, string Fire, string Work);
/// <summary>The authored source location for one contributor line.</summary>
public sealed record BrowserCostSource(string Name, bool IsInteraction, string JsonPointer, string? SourcePath,
    int? Line, int? Column, string? ModuleInstancePath);
/// <summary>Static resource dimensions, with byte counts kept exact on the JavaScript wire.</summary>
public sealed record BrowserResourceDimensions(
    int RowCount, int TopologyCount, int PopulationCapacity, int CellSlotCount, string VectorComponentBytes,
    int LaneSlotCount, int LaneRosterCount, int DrawMaskWordCount, string? LayoutBytes, string? RetainedVisibilityBytes,
    string? RetainedKeyBytes, string? ArenaFootprintBytes, string ArenaAdmissionCeilingBytes,
    string JournalAllowanceBytes, string? MeasurementIssue, string UnmodeledTotalMemoryReason
);
/// <summary>The shared authored cost report projected onto the browser wire. Preview-host capability refusals
/// do not reduce the document's cost; the same definition has the server's report.</summary>
public sealed record BrowserCostReport(
    string ModelId, string? EvidenceDigest, string Scope, int SimulationRateHz, string StepAllowanceCycles,
    BrowserCostBound RecurringBound, BrowserCostBound SearchReservations, BrowserCostBound TotalBound,
    BrowserCostBound EditBurstBound, bool Admitted, string HeuristicWorkUnitsPerTick,
    IReadOnlyList<BrowserCostContributor> Contributors, IReadOnlyList<BrowserCostSource> ContributorSources,
    IReadOnlyList<string> Issues, BrowserResourceDimensions Resources
) {
    /// <summary>Projects an existing analysis without compiling or repricing any program.</summary>
    public static BrowserCostReport From(WorldCostReport report) => new(
        report.ModelId, report.EvidenceDigest, report.Scope, report.SimulationRateHz,
        report.StepAllowanceCycles.ToString(provider: CultureInfo.InvariantCulture),
        BrowserCostBound.From(bound: report.RecurringBound), BrowserCostBound.From(bound: report.SearchReservations),
        BrowserCostBound.From(bound: report.TotalBound), BrowserCostBound.From(report.EditBurstBound), report.Admitted,
        report.HeuristicWorkUnitsPerTick.ToString(),
        report.Contributors.Select(selector: static line => new BrowserCostContributor(
            line.Name, line.IsInteraction, line.Multiplier.ToString(provider: CultureInfo.InvariantCulture),
            line.Cost.Setup.ToString(), line.Cost.Check.ToString(), line.Cost.Effects.ToString(), line.WorkUnits.ToString()
        )).ToArray(),
        report.ContributorSources.Select(static source => new BrowserCostSource(
            source.Name, source.IsInteraction, source.JsonPointer, source.SourcePath, source.Line, source.Column,
            source.ModuleInstancePath
        )).ToArray(),
        report.Issues,
        new BrowserResourceDimensions(
            report.Resources.RowCount, report.Resources.TopologyCount, report.Resources.PopulationCapacity,
            report.Resources.CellSlotCount, report.Resources.VectorComponentBytes.ToString(provider: CultureInfo.InvariantCulture),
            report.Resources.LaneSlotCount, report.Resources.LaneRosterCount, report.Resources.DrawMaskWordCount,
            Text(report.Resources.LayoutBytes), Text(report.Resources.RetainedVisibilityBytes),
            Text(report.Resources.RetainedKeyBytes), Text(report.Resources.ArenaFootprintBytes),
            report.Resources.ArenaAdmissionCeilingBytes.ToString(provider: CultureInfo.InvariantCulture),
            report.Resources.JournalAllowanceBytes.ToString(provider: CultureInfo.InvariantCulture),
            report.Resources.MeasurementIssue, report.Resources.UnmodeledTotalMemoryReason
        )
    );

    private static string? Text(long? value) => value?.ToString(provider: CultureInfo.InvariantCulture);
}
