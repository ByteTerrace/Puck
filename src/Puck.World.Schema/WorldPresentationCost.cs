using System.Globalization;

namespace Puck.World;

/// <summary>One frame-graph instance's price in the cost report: its extent ceiling, its rate and its passes.
/// <para>
/// Its extent follows its on-screen footprint each frame, so a document prices only its ceiling: one display. A render
/// costs its passes times its pixels, so the instance costs at most <see cref="PassDisplaysPerFrame"/> displays of
/// pass-pixels each presented frame.
/// </para>
/// </summary>
/// <param name="Name">The <c>views.graphs</c> row.</param>
/// <param name="Source">The graph document it renders.</param>
/// <param name="Divisor">The frames per render it declares, or <see langword="null"/> when it declares a rate in
/// hertz.</param>
/// <param name="Hertz">The renders a second it declares at most, or <see langword="null"/> when it declares a
/// divisor.</param>
/// <param name="Passes">The passes one render records, or <see langword="null"/> when its graph was not planned.</param>
/// <param name="Issue">Why <paramref name="Passes"/> is unknown, or <see langword="null"/>.</param>
public sealed record WorldGraphInstanceCost(string Name, string Source, int? Divisor, int? Hertz, int? Passes, string? Issue) {
    /// <summary>Gets the most pass-pixels a presented frame spends on the instance, in displays: its passes over its
    /// divisor, or <see langword="null"/> when its passes are unknown or its rate is in hertz, whose frames depend on
    /// the display's rate.</summary>
    public double? PassDisplaysPerFrame => (((Passes is { } passes) && (Divisor is { } divisor))
        ? (((double)passes) / divisor)
        : null
    );

    /// <summary>Describes the instance's rate: <c>every frame</c>, <c>1/N frames</c>, or <c>N Hz</c>.</summary>
    /// <returns>The description.</returns>
    public string DescribeRate() => ((Hertz is { } hertz)
        ? string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"{hertz} Hz"
        )
        : (((Divisor ?? 1) == 1)
            ? "every frame"
            : string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"1/{Divisor} frames"
            ))
    );
}
/// <summary>The cost report's presentation dimension: every frame-graph instance a document declares and the
/// scheduler's price ceiling. Priced in pass-pixels, never in time.</summary>
/// <param name="Instances">One line per <c>views.graphs</c> row, in row order.</param>
/// <param name="PassPixelsPerFrame">The authored price ceiling on the instances the display does not show directly, or
/// 0 for none.</param>
public sealed record WorldPresentationCost(IReadOnlyList<WorldGraphInstanceCost> Instances, long PassPixelsPerFrame) {
    /// <summary>The issue a line carries when the analysis read no graph source.</summary>
    public const string UnplannedIssue = "the document analysis plans no graph source";

    /// <summary>Gets the dimension of a document that declares no graph instance.</summary>
    public static WorldPresentationCost None { get; } = new(
        Instances: [],
        PassPixelsPerFrame: 0
    );

    /// <summary>Prices a document's graph instances.</summary>
    /// <param name="definition">The document.</param>
    /// <param name="passes">Plans a row's graph, returning its pass count or why it has none; <see langword="null"/>
    /// prices every line with <see cref="UnplannedIssue"/>.</param>
    /// <returns>The dimension.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static WorldPresentationCost Measure(WorldDefinition definition, Func<WorldViewGraph, (int? Passes, string? Issue)>? passes = null) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var graphs = (definition.Views.Graphs ?? []);

        if (
            (graphs.Count == 0) &&
            (definition.Views.GraphBudget is null)
        ) {
            return None;
        }

        var lines = new WorldGraphInstanceCost[graphs.Count];

        for (var index = 0; (index < graphs.Count); index++) {
            var graph = graphs[index];
            var planned = ((passes is null)
                ? (Passes: null, Issue: UnplannedIssue)
                : passes(arg: graph)
            );
            var refresh = WorldViewGraphs.RefreshOf(graph: graph);

            lines[index] = new WorldGraphInstanceCost(
                Divisor: ((refresh.Hertz == 0)
                    ? refresh.Divisor
                    : null),
                Hertz: ((refresh.Hertz == 0)
                    ? null
                    : refresh.Hertz),
                Issue: planned.Issue,
                Name: graph.Name,
                Passes: planned.Passes,
                Source: (graph.Source ?? graph.Package!)
            );
        }

        return new WorldPresentationCost(
            Instances: lines,
            PassPixelsPerFrame: (definition.Views.GraphBudget?.PassPixelsPerFrame ?? 0)
        );
    }
    /// <summary>Describes the dimension as one <c>world.budget</c> clause: each instance with its extent ceiling, rate
    /// and passes, then the price ceiling.</summary>
    /// <returns>The clause.</returns>
    public string Describe() {
        if (Instances.Count == 0) {
            return "graphs none";
        }

        var lines = Instances.Select(selector: static line => string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"{line.Name} ({line.Source}) extent<=display rate {line.DescribeRate()} passes {((line.Passes is { } passes) ? passes.ToString(provider: CultureInfo.InvariantCulture) : $"? ({line.Issue})")}{((line.PassDisplaysPerFrame is { } displays) ? $" <= {displays:0.###} display pass-pixels/frame" : string.Empty)}"
        ));

        return string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"graphs {Instances.Count} instance(s): {string.Join(separator: "; ", values: lines)}; budget {((PassPixelsPerFrame == 0) ? "unbounded" : $"{PassPixelsPerFrame} pass-pixels/frame")}"
        );
    }
}
public sealed partial record WorldCostReport {
    /// <summary>Gets the presentation dimension: every frame-graph instance with its extent ceiling, rate and passes.
    /// A document-only analysis leaves passes unplanned; a host that reads the graph sources prices them.</summary>
    public WorldPresentationCost Presentation { get; init; } = WorldPresentationCost.None;
}
