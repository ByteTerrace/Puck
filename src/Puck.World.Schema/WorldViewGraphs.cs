using Puck.Hosting;

namespace Puck.World;

/// <summary>Reads <c>views.graphs</c> rows as the render-graph instances the scheduler plans: one instance per row,
/// reading the instances its inputs name.</summary>
public static class WorldViewGraphs {
    /// <summary>The name of the instance that renders the SDF world, the <c>sdf.world</c> producer the root reads,
    /// before any post pass or the overlay is drawn over it. A <c>captures</c> row names it to capture the world
    /// alone.</summary>
    public const string WorldInstance = "world";
    /// <summary>The name of the root graph instance composition synthesizes when the world has a pass to draw over its
    /// SDF world: each <c>render.extensions</c> pass in order, then the overlay.</summary>
    public const string MainInstance = "main";

    /// <summary>Returns a row's refresh as the scheduler reads it: every frame when the row authors none, and an
    /// invalid refresh when it authors both or neither rate inside its <c>refresh</c>.</summary>
    /// <param name="graph">The row.</param>
    /// <returns>The refresh.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="graph"/> is <see langword="null"/>.</exception>
    public static RenderGraphRefresh RefreshOf(WorldViewGraph graph) {
        ArgumentNullException.ThrowIfNull(argument: graph);

        return ((graph.Refresh is { } refresh)
            ? new RenderGraphRefresh(
                Divisor: (refresh.Divisor ?? 0),
                Hertz: (refresh.Hertz ?? 0)
            )
            : RenderGraphRefresh.EveryFrame
        );
    }
    /// <summary>Returns the rows as render-graph instances, in row order. Several inputs bound to one producer are one
    /// read, a previous-frame read only when every one of them is.</summary>
    /// <param name="graphs">The rows.</param>
    /// <param name="passes">The passes one render of a row's graph records.</param>
    /// <returns>The instances.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="graphs"/> or <paramref name="passes"/> is
    /// <see langword="null"/>.</exception>
    public static IReadOnlyList<RenderGraphInstance> Instances(IReadOnlyList<WorldViewGraph> graphs, Func<WorldViewGraph, int> passes) {
        ArgumentNullException.ThrowIfNull(argument: graphs);
        ArgumentNullException.ThrowIfNull(argument: passes);

        var instances = new RenderGraphInstance[graphs.Count];

        for (var index = 0; (index < graphs.Count); index++) {
            var graph = graphs[index];

            instances[index] = new RenderGraphInstance(
                Name: graph.Name,
                Passes: passes(arg: graph),
                Reads: [.. (graph.Inputs ?? [])
                    .GroupBy(
                        comparer: StringComparer.Ordinal,
                        keySelector: static input => input.Instance
                    )
                    .Select(selector: static group => new RenderGraphRead(
                        PreviousFrame: group.All(predicate: static input => input.PreviousFrame),
                        Producer: group.Key
                    ))],
                Refresh: RefreshOf(graph: graph)
            );
        }

        return instances;
    }
}
