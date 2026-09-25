using System.Diagnostics.CodeAnalysis;
using Puck.Hosting;

namespace Puck.Shaders;

/// <summary>The instance-set half of a render-graph runtime a host drives while it runs: replacing the set, installing a
/// compiled graph on one instance, and reading the node an instance renders through. <see cref="RenderGraphRuntime"/>
/// implements it; a host that owns a world's rows drives it without a device.</summary>
public interface IRenderGraphInstances {
    /// <summary>Gets the name of the instance the display shows and captures read.</summary>
    string Root { get; }

    /// <summary>Returns the node a graph instance renders through, or <see langword="null"/> when the set has no graph
    /// instance of that name.</summary>
    /// <param name="instance">The instance's name.</param>
    /// <returns>The node.</returns>
    ShaderPipelineRenderNode? NodeOf(string instance);
    /// <summary>Installs a graph on one graph instance, keeping the graph it has when the graph is refused.</summary>
    /// <param name="instance">The graph instance's name.</param>
    /// <param name="graph">The graph.</param>
    /// <param name="refusal">Why the graph was refused, when this returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the instance's node builds the graph.</returns>
    bool TryInstall(string instance, RenderGraphRuntimeGraph graph, [NotNullWhen(returnValue: false)] out RenderGraphRuntimeRefusal? refusal);
    /// <summary>Replaces the instance set, keeping every instance the new set continues by name and kind.</summary>
    /// <param name="set">The new instances.</param>
    /// <param name="graphs">Each new instance's graph, parallel to the set: <see langword="null"/> keeps a kept
    /// instance's graph and leaves a new one without a graph.</param>
    /// <param name="root">The name of the instance the display shows.</param>
    /// <param name="refusal">Why the set was refused, when this returns <see langword="false"/>; the set is unchanged
    /// then.</param>
    /// <returns><see langword="true"/> when the new set runs from the next frame.</returns>
    bool TryReconfigure(RenderGraphInstanceSet set, IReadOnlyList<RenderGraphRuntimeGraph?> graphs, string root, [NotNullWhen(returnValue: false)] out RenderGraphRuntimeRefusal? refusal);
}
