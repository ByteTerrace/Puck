using Puck.Transpiler.Diagnostics;

namespace Puck.World.Transpiler;

/// <summary>Connects the shared authored cost report to a standalone source compilation's defining locations.</summary>
public static class WorldCostAnalysis {
    /// <summary>Analyzes a successfully expanded standalone source, without installing an arena or applying
    /// admission ceilings. Unresolved runtime basis/import composition must be completed before this operation.</summary>
    /// <param name="compilation">The successful source compilation to analyze.</param>
    /// <returns>The report with JSON pointers, defining source spans, and module instance paths.</returns>
    /// <exception cref="InvalidOperationException">The source is incomplete, still requires document composition,
    /// or cannot be decoded and compiled as a world definition.</exception>
    public static WorldCostReport Generate(WorldCompilation compilation) {
        ArgumentNullException.ThrowIfNull(compilation);
        if (!WorldJsonPayload.TryParse(
            deferDrawSites: true,
            json: compilation.RequireJson().ToJsonString(),
            info: WorldJsonContext.Default.WorldDefinition,
            value: out var definition,
            error: out var reason)) {
            throw new InvalidOperationException(message: reason);
        }
        if ((definition.Basis is not null) || (definition.Imports is { Count: > 0 })) {
            throw new InvalidOperationException(message: "Cost analysis requires the complete composed document, not a source with unresolved basis or imports.");
        }
        if (!WorldDrawBootResolver.TryResolve(definition: definition, instanceIdentity: WorldDefinitionLoader.BootInstanceName, reason: out reason, resolved: out var drawn)
            || !WorldStateDocumentValues.TryResolve(drawn, out reason)) {
            throw new InvalidOperationException(message: reason);
        }
        return Attach(report: WorldCostReport.Generate(drawn), sourceMap: compilation.SourceMap);
    }
    /// <summary>Resolves contributor pointers through the source map for the exact document already analyzed.
    /// This does not recompile or reprice programs.</summary>
    /// <param name="report">The report over the source map's exact expanded document.</param>
    /// <param name="sourceMap">The map emitted when that document was compiled.</param>
    /// <returns>A report retaining the same costs and contributors, with resolved source locations.</returns>
    public static WorldCostReport Attach(WorldCostReport report, SourceMap sourceMap) {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(sourceMap);
        var sources = new WorldCostSource[report.ContributorSources.Count];

        for (var index = 0; (index < sources.Length); index++) {
            var source = report.ContributorSources[index];

            sources[index] = (sourceMap.TryGetOrigin(jsonPointer: source.JsonPointer, origin: out var origin)
                ? source with {
                    SourcePath = origin.SourcePath,
                    Line = origin.Span.Line,
                    Column = origin.Span.Column,
                    ModuleInstancePath = origin.ModuleInstancePath,
                }
                : source);
        }
        return report with { ContributorSources = Array.AsReadOnly(array: sources) };
    }
}
