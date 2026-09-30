namespace Puck.Shaders;

/// <summary>A package instance's render grid inside its output: a ceiling allocated with the graph and a current grid
/// that can change without rebuilding. Both are positive and no larger than the output; the current grid is no larger
/// than the ceiling. The package owns any scale quantization.</summary>
public interface IShaderPipelineRenderExtent {
    /// <summary>Gets the revision of every input the graph's allocation and passes depend on: the ceiling, and whatever
    /// else chooses which graph the instance runs (<see cref="IRenderGraphPackageFactory.FragmentOf"/>), so no graph
    /// records a grid it was not built for. A change rebuilds beside the installed graph, which presents its last image
    /// until the replacement installs.</summary>
    long Revision { get; }

    /// <summary>Resolves the allocation ceiling against the output.</summary>
    /// <param name="width">The output width.</param>
    /// <param name="height">The output height.</param>
    /// <returns>The allocated render extent.</returns>
    (uint Width, uint Height) CeilingAt(uint width, uint height);
    /// <summary>Resolves this frame's render grid against the output.</summary>
    /// <param name="width">The output width.</param>
    /// <param name="height">The output height.</param>
    /// <returns>The current render extent, bounded by the ceiling.</returns>
    (uint Width, uint Height) FrameAt(uint width, uint height);
}
