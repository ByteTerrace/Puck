using System.Text.Json;
using Puck.Shaders;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>The <c>pipeline.overrides</c> door's refusal vocabulary: every reason a parameter-override commit, or a
/// <c>views.graphs</c> row upsert that names overrides or an output, can be refused with. A refusal reads
/// <c>pipeline.overrides/&lt;id&gt;: &lt;detail&gt;</c>, the spelling <c>world.refusals</c> lists it by.</summary>
public enum WorldPipelineOverrideRefusal : byte {
    /// <summary>No <c>views.graphs</c> row with a source names the committed instance.</summary>
    [Refusal(door: "pipeline.overrides", condition: "no views.graphs row with a source names the committed instance", kind: RefusalKind.Verdict)]
    InstanceUnknown,

    /// <summary>The row's revision differs from the revision the preview was based on.</summary>
    [Refusal(door: "pipeline.overrides", condition: "the row's revision differs from the revision the preview was based on", kind: RefusalKind.Verdict)]
    RevisionStale,

    /// <summary>The server has no pipeline source reader, so it cannot bind the values.</summary>
    [Refusal(door: "pipeline.overrides", condition: "the server has no pipeline source reader attached", kind: RefusalKind.Verdict)]
    SourcesUnattached,

    /// <summary>The row's source cannot be read or declares no pipeline.</summary>
    [Refusal(door: "pipeline.overrides", condition: "the row's source cannot be read or declares no pipeline", kind: RefusalKind.Verdict)]
    SourceUnreadable,

    /// <summary>The source's content differs from the source the installed graph was compiled from.</summary>
    [Refusal(door: "pipeline.overrides", condition: "the source's content differs from the source the installed graph was compiled from", kind: RefusalKind.Verdict)]
    SourceChanged,

    /// <summary>The source's parameter schemas differ from the installed graph's.</summary>
    [Refusal(door: "pipeline.overrides", condition: "the source's parameter schemas differ from the installed graph's", kind: RefusalKind.Verdict)]
    ConfigIncompatible,

    /// <summary>An override does not bind through its pass's config schema.</summary>
    [Refusal(door: "pipeline.overrides", condition: "an override does not bind through its pass's config schema", kind: RefusalKind.Verdict)]
    OverrideUnbound,

    /// <summary>The selected output names no image version of the source.</summary>
    [Refusal(door: "pipeline.overrides", condition: "the selected output names no image version of the source", kind: RefusalKind.Verdict)]
    OutputUndeclared,

    /// <summary>A bound parameter names no scalar config field of its pass.</summary>
    [Refusal(door: "pipeline.overrides", condition: "a bound parameter names no scalar config field of its pass", kind: RefusalKind.Verdict)]
    ParameterUnbound,
}
/// <summary>Reads the sources <c>views.graphs</c> rows name, for the server's override gate: a graph document, a
/// one-off shader, or a package directory, read by <see cref="ShaderPipelineSource.TryRead"/>. Rows resolve against
/// one document directory, the same directory the rendering host compiles them against, so the server and the host
/// read the same file for a row.</summary>
/// <param name="documentDirectory">The directory a row's relative source resolves against
/// (<see cref="WorldDefinition.DocumentDirectory"/>), or <see langword="null"/> for a document with none, whose relative
/// sources are refused by name.</param>
public sealed class WorldPipelineSources(string? documentDirectory) {
    /// <summary>Gets the directory a row's relative source resolves against, or <see langword="null"/> when the
    /// document has none.</summary>
    public string? DocumentDirectory { get; } = ((documentDirectory is null)
        ? null
        : Abstractions.PuckPaths.Normalize(path: documentDirectory));

    /// <summary>Reads the source a row names.</summary>
    /// <param name="graph">The graph row.</param>
    /// <param name="source">The read, when this returns <see langword="true"/>.</param>
    /// <param name="reason">Why the source could not be read, such as a row that names a package instead.</param>
    /// <returns><see langword="true"/> when the source was read.</returns>
    public bool TryRead(WorldViewGraph graph, out ShaderPipelineSource? source, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: graph);

        if (graph.Source is not { } authored) {
            source = null;
            reason = $"'{graph.Name}' names package '{graph.Package}', which has no source";

            return false;
        }
        if (!WorldDocumentPaths.TryResolve(
            documentDirectory: DocumentDirectory,
            path: authored,
            reason: out reason,
            resolved: out var path
        )) {
            source = null;

            return false;
        }

        return ShaderPipelineSource.TryRead(
            name: graph.Name,
            path: path,
            reason: out reason,
            source: out source
        );
    }
    /// <summary>Plans the graph document a <c>views.graphs</c> row names against the packages this build ships, for
    /// the cost report's presentation dimension.</summary>
    /// <param name="graph">The graph row.</param>
    /// <returns>The passes one render records, or <see langword="null"/> with why the graph could not be planned. A
    /// graph that plans but has an input the row binds to no external version returns its passes and names the
    /// input.</returns>
    public (int? Passes, string? Issue) PlanGraph(WorldViewGraph graph) {
        ArgumentNullException.ThrowIfNull(argument: graph);

        if (graph.Package is { } package) {
            return (null, $"package '{package}' renders through its producer, which the host prices");
        }
        if (!WorldDocumentPaths.TryResolve(
            documentDirectory: DocumentDirectory,
            path: graph.Source!,
            reason: out var unresolved,
            resolved: out var path
        )) {
            return (null, unresolved);
        }

        if (!RenderGraphSource.TryPlan(
            packages: RenderGraphPackageCatalog.Shipped,
            path: path,
            plan: out var plan,
            reason: out var reason
        )) {
            return (null, reason);
        }

        var unbound = (graph.Inputs ?? [])
            .Where(predicate: input => !plan.Inputs.Contains(value: input.Resource))
            .Select(selector: static input => input.Resource)
            .ToArray();

        return (plan.Steps.Count, ((unbound.Length == 0)
            ? null
            : $"input(s) {string.Join(
                separator: ", ",
                values: unbound
            )} name no external version of the graph"));
    }
}
public sealed partial class WorldServer {
    /// <summary>Gets or sets the reader the override gate binds <c>views.graphs</c> overrides through, or
    /// <see langword="null"/> when this server reads no pipeline sources and refuses every override by name. A
    /// composition root attaches it whether or not it renders, so a headless and a rendered host accept the same
    /// commits.</summary>
    public WorldPipelineSources? PipelineSources { get; set; }

    /// <summary>Binds every <c>views.graphs</c> row of the installed document that names overrides or an output
    /// against its source, as a <c>world.load</c> or <c>world.reload</c> binds the document it loads. A composition root
    /// calls it once it attaches <see cref="PipelineSources"/>, so a booted document's bad value is refused by name at
    /// boot rather than reported when a graph installs.</summary>
    /// <param name="reason">The first refusal, in row order, spelled as the <c>pipeline.overrides</c> door spells it.</param>
    /// <returns><see langword="true"/> when every such row binds.</returns>
    public bool TryBindPipelineRows(out string reason) => m_document.TryBindPipelineRows(
        candidate: Definition,
        reason: out reason
    );
}
public sealed partial class WorldDocument {
    private static string RefuseOverride(WorldPipelineOverrideRefusal refusal, string detail) => $"pipeline.overrides/{refusal}: {detail}";
    // Whether a parameter's value fills the member it binds. A scalar field reads one cell, so a token naming a keyed row
    // must name a key. An array reads a whole keyed row (WorldBoundRow): a Bool, Int or Fixed row no longer than the
    // array, whose values the element type holds exactly; an integer element takes only an Int or Bool row whose declared
    // bounds lie in its range, and a Fixed row fills only a float element.
    private static bool TryFitParameter(ShaderArrayField? array, BindableScalar value, WorldDefinition definition, out string reason) {
        reason = string.Empty;

        if (array is null) {
            if (
                (value.State is { Key: null } cell) &&
                WorldBoundRow.TryResolve(
                definition: definition,
                length: out _,
                row: out _,
                rowName: cell.Row
            )
            ) {
                reason = $"'{cell.Row}' is a keyed row, and a scalar field reads one cell; name its key, or bind an array.";

                return false;
            }

            return true;
        }
        if (value.State is not { Key: null } binding) {
            reason = "an array binds a whole row: a state.<row> token naming no key.";

            return false;
        }
        if (!WorldBoundRow.TryResolve(
            definition: definition,
            length: out var length,
            row: out var row,
            rowName: binding.Row
        )) {
            reason = $"'{binding.Row}' names no keyed state row.";

            return false;
        }
        if (length > array.Length) {
            reason = $"row '{binding.Row}' presents {length} elements and the array holds {array.Length}.";

            return false;
        }

        var fits = ((row.Kind, array.Type) switch {
            (CellKind.Bool, _) => true,
            (CellKind.Fixed, ShaderValueType.Float) => true,
            (CellKind.Int, ShaderValueType.Float) => true,
            (CellKind.Int, ShaderValueType.Int) => ((row.Min is { } min) && (row.Max is { } max) && (min >= int.MinValue) && (max <= int.MaxValue)),
            (CellKind.Int, ShaderValueType.Uint) => ((row.Min is { } min) && (row.Max is { } max) && (min >= 0L) && (max <= uint.MaxValue)),
            _ => false,
        });

        if (!fits) {
            reason = $"row '{binding.Row}' holds {row.Kind} values{((row.Kind == CellKind.Int) ? $" bounded [{(row.Min?.ToString(provider: System.Globalization.CultureInfo.InvariantCulture) ?? "-")}, {(row.Max?.ToString(provider: System.Globalization.CultureInfo.InvariantCulture) ?? "-")}]" : string.Empty)}, which a {array.Type} element cannot hold exactly; declare the row's bounds within the element's range.";

            return false;
        }

        return true;
    }

    // The load gate: a whole document's rows that name overrides or an output bind against their sources, the check a
    // mutation applies to the rows it changes. sourcesOverride lets a rebuild validate the CANDIDATE document's rows
    // against the directory IT will resolve against once installed, rather than Host.PipelineSources, which still
    // names the directory of the document currently installed — the candidate is not installed yet when this runs.
    internal bool TryBindPipelineRows(WorldDefinition candidate, out string reason, WorldPipelineSources? sourcesOverride = null) {
        reason = string.Empty;

        foreach (var row in (candidate.Views.Graphs ?? [])) {
            if (
                (row.Source is not null) &&
                ((row.Overrides is not null) || (row.Output is not null) || (row.Parameters is not null)) &&
                !TryBindPipelineRow(
                commit: null,
                definition: candidate,
                reason: out reason,
                row: row,
                sourcesOverride: sourcesOverride
            )) {
                return false;
            }
        }

        return true;
    }

    // A commit composes against the row as it stands here, keeping every member it does not carry. The staleness check
    // is the row's own fingerprint, so an unrelated edit elsewhere in the document never stales a preview.
    private static bool TryComposeGraphCommit(WorldDefinition current, WorldMutation.CommitViewGraph commit, out WorldDefinition candidate, out string reason) {
        candidate = current;

        var views = current.Views;

        if (
            (WorldDefinitionRows.FindGraph(
                graphs: views.Graphs,
                name: commit.Name
            ) is not { } row) ||
            (row.Source is null)
        ) {
            reason = RefuseOverride(
                detail: $"no views.graphs row with a source named '{commit.Name}'",
                refusal: WorldPipelineOverrideRefusal.InstanceUnknown
            );

            return false;
        }

        var revision = WorldDefinitionFingerprint.ComputeGraph(graph: row);

        if (!string.Equals(
            a: revision,
            b: commit.Revision,
            comparisonType: StringComparison.Ordinal
        )) {
            reason = RefuseOverride(
                detail: $"'{commit.Name}' is at revision {revision}, not the previewed {commit.Revision}; preview again",
                refusal: WorldPipelineOverrideRefusal.RevisionStale
            );

            return false;
        }

        Dictionary<string, JsonElement>? overrides = null;

        if (commit.Overrides is { Count: > 0 } committed) {
            overrides = new Dictionary<string, JsonElement>(comparer: StringComparer.Ordinal);

            foreach (var (pass, config) in committed.OrderBy(keySelector: static pair => pair.Key, comparer: StringComparer.Ordinal)) {
                overrides[pass] = config.Clone();
            }
        }

        candidate = (current with {
            ViewsRaw = (views with {
                Graphs = Upsert(
                    item: (row with {
                        Output = commit.Output,
                        Overrides = overrides,
                        TimeScale = commit.TimeScale,
                    }),
                    keyOf: static graph => graph.Name,
                    list: (views.Graphs ?? [])
                ),
            }),
        });
        reason = string.Empty;

        return true;
    }
    // The gate every mutation passes after validation. A row with a source whose source, overrides or output the
    // candidate changed, and that names overrides or an output, has its source read and its values bound through the source's config
    // schema, whichever kind carried it. A commit's source must also still be the one its installed graph was compiled
    // from.
    private bool TryAdmitPipelineOverrides(WorldMutation mutation, WorldDefinition current, WorldDefinition candidate, out string reason) {
        reason = string.Empty;

        if (ReferenceEquals(
            objA: candidate.ViewsRaw,
            objB: current.ViewsRaw
        )) {
            return true;
        }

        var commit = (mutation as WorldMutation.CommitViewGraph);

        foreach (var row in (candidate.Views.Graphs ?? [])) {
            if (row.Source is null) {
                continue;
            }

            var previous = WorldDefinitionRows.FindGraph(
                graphs: current.Views.Graphs,
                name: row.Name
            );
            var committing = ((commit is not null) && string.Equals(
                a: commit.Name,
                b: row.Name,
                comparisonType: StringComparison.Ordinal
            ));

            if (
                !committing &&
                ((row.Overrides is null) && (row.Output is null) && (row.Parameters is null))
            ) {
                continue;
            }
            if (
                !committing &&
                (previous is not null) &&
                ReferenceEquals(
                objA: previous.Overrides,
                objB: row.Overrides
            ) &&
                ReferenceEquals(
                objA: previous.Parameters,
                objB: row.Parameters
            ) &&
                string.Equals(
                a: previous.Output,
                b: row.Output,
                comparisonType: StringComparison.Ordinal
            ) &&
                string.Equals(
                a: previous.Source,
                b: row.Source,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                continue;
            }
            if (!TryBindPipelineRow(
                commit: (committing
                    ? commit
                    : null),
                definition: candidate,
                reason: out reason,
                row: row
            )) {
                return false;
            }
        }

        return true;
    }
    private bool TryBindPipelineRow(WorldViewGraph row, WorldDefinition definition, WorldMutation.CommitViewGraph? commit, out string reason, WorldPipelineSources? sourcesOverride = null) {
        reason = string.Empty;

        if ((sourcesOverride ?? Host.PipelineSources) is not { } sources) {
            reason = RefuseOverride(
                detail: "this server reads no pipeline sources, so it cannot bind the values",
                refusal: WorldPipelineOverrideRefusal.SourcesUnattached
            );

            return false;
        }
        if (!sources.TryRead(
            graph: row,
            reason: out var readReason,
            source: out var source
        )) {
            reason = RefuseOverride(
                detail: readReason,
                refusal: WorldPipelineOverrideRefusal.SourceUnreadable
            );

            return false;
        }
        if (commit is not null) {
            if (!string.Equals(
                a: source!.SourceIdentity,
                b: commit.SourceIdentity,
                comparisonType: StringComparison.Ordinal
            )) {
                reason = RefuseOverride(
                    detail: $"'{row.Source}' is now {source.SourceIdentity}, not the installed {commit.SourceIdentity}; let the edit install and preview again",
                    refusal: WorldPipelineOverrideRefusal.SourceChanged
                );

                return false;
            }
            if (!string.Equals(
                a: source.ConfigIdentity,
                b: commit.ConfigIdentity,
                comparisonType: StringComparison.Ordinal
            )) {
                reason = RefuseOverride(
                    detail: $"'{row.Source}' declares parameter schemas {source.ConfigIdentity}, not the installed {commit.ConfigIdentity}",
                    refusal: WorldPipelineOverrideRefusal.ConfigIncompatible
                );

                return false;
            }
        }
        if (!source!.TryBindOverrides(
            overrides: row.Overrides,
            reason: out var bindReason
        )) {
            reason = RefuseOverride(
                detail: $"'{row.Name}' {bindReason}",
                refusal: WorldPipelineOverrideRefusal.OverrideUnbound
            );

            return false;
        }
        foreach (var (pass, fields) in (row.Parameters ?? new Dictionary<string, IReadOnlyDictionary<string, BindableScalar>>())) {
            foreach (var (field, value) in fields) {
                if (
                    !source.TryCheckParameter(
                    array: out var array,
                    field: field,
                    passName: pass,
                    reason: out var parameterReason
                ) ||
                    !TryFitParameter(
                    array: array,
                    definition: definition,
                    reason: out parameterReason,
                    value: value
                )
                ) {
                    reason = RefuseOverride(
                        detail: $"'{row.Name}' parameter {pass}.{field}: {parameterReason}",
                        refusal: WorldPipelineOverrideRefusal.ParameterUnbound
                    );

                    return false;
                }
            }
        }
        if (
            (row.Output is { } output) &&
            !source.DeclaresImage(name: output)
        ) {
            reason = RefuseOverride(
                detail: $"'{output}' is not an image version of '{row.Source}'",
                refusal: WorldPipelineOverrideRefusal.OutputUndeclared
            );

            return false;
        }

        return true;
    }
}
