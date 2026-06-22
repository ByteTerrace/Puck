using System.Text.Json.Serialization;

namespace Puck.Demo;

/// <summary>
/// The cross-backend graphics parity gate's machine-readable report (<c>artifacts/parity/report.json</c>): the
/// authoritative per-mode numbers behind the amplified diff heatmaps. Source-generated (see
/// <see cref="ParityReportJsonContext"/>) so the gate's JSON write stays reflection-free, like every other Puck JSON path.
/// </summary>
internal sealed record ParityReport {
    /// <summary>The overall verdict across all debug-view modes (<c>"pass"</c>/<c>"fail"</c>).</summary>
    public required string Verdict { get; init; }
    /// <summary>The compared frame width in pixels.</summary>
    public required int Width { get; init; }
    /// <summary>The compared frame height in pixels.</summary>
    public required int Height { get; init; }
    /// <summary>The per-debug-view-mode reports, keyed by mode name.</summary>
    public required IReadOnlyDictionary<string, ParityModeReport> Modes { get; init; }
}

/// <summary>One debug-view mode's parity result.</summary>
internal sealed record ParityModeReport {
    /// <summary>The mode's verdict (<c>"pass"</c>/<c>"fail"</c>).</summary>
    public required string Verdict { get; init; }
    /// <summary>The threshold checks this mode failed (empty on a pass).</summary>
    public required IReadOnlyList<string> Failures { get; init; }
    /// <summary>The measured cross-backend difference metrics.</summary>
    public required ParityMetricsReport Metrics { get; init; }
    /// <summary>The PASS thresholds the metrics were evaluated against.</summary>
    public required ParityThresholdsReport Thresholds { get; init; }
}

/// <summary>The measured per-mode cross-backend difference metrics.</summary>
internal sealed record ParityMetricsReport {
    /// <summary>The total pixels compared.</summary>
    public required int TotalPixels { get; init; }
    /// <summary>The pixels that differ in any channel.</summary>
    public required int DifferingPixels { get; init; }
    /// <summary>The percentage of differing pixels.</summary>
    public required double PercentDiffering { get; init; }
    /// <summary>The largest single-channel delta.</summary>
    public required int MaxChannelDelta { get; init; }
    /// <summary>The mean absolute per-channel error.</summary>
    public required double MeanAbsError { get; init; }
    /// <summary>The fraction of differing pixels with no differing neighbour (isolated ±1 noise).</summary>
    public required double IsolatedFraction { get; init; }
    /// <summary>The fraction of differing pixels whose max delta is exactly 1.</summary>
    public required double UnitDeltaFraction { get; init; }
    /// <summary>The per-delta-value histogram of differing pixels.</summary>
    public required int[] DeltaHistogram { get; init; }
}

/// <summary>The PASS thresholds as reported (the field names mirror the report's keys, not the threshold-set's).</summary>
internal sealed record ParityThresholdsReport {
    /// <summary>The maximum permitted single-channel delta.</summary>
    public required int MaxChannelDelta { get; init; }
    /// <summary>The maximum permitted percentage of differing pixels.</summary>
    public required double PercentDiffering { get; init; }
    /// <summary>The minimum required unit-delta fraction.</summary>
    public required double UnitDeltaFraction { get; init; }
    /// <summary>The minimum required isolated fraction.</summary>
    public required double IsolatedFraction { get; init; }
    /// <summary>The maximum permitted mean absolute error.</summary>
    public required double MeanAbsError { get; init; }
}

/// <summary>The source-generation context for <see cref="ParityReport"/> — the reflection-free serializer the gate
/// writes <c>report.json</c> through.</summary>
[JsonSerializable(typeof(ParityReport))]
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class ParityReportJsonContext : JsonSerializerContext {
}
