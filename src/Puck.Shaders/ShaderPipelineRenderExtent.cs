namespace Puck.Shaders;

/// <summary>A package instance's render grid: a ceiling allocated with the graph and a current grid that can change
/// without rebuilding. Both are positive; the current grid is no larger than the ceiling. Graphs exporting any image
/// also bound the ceiling by their output extent. An all-buffer graph has no output pixel grid, so its package owns
/// the ceiling independently of the scheduler's placeholder extent. The package owns any scale quantization.</summary>
public interface IShaderPipelineRenderExtent {
    /// <summary>Gets the revision of every input the graph's allocation and passes depend on: the ceiling, and whatever
    /// else chooses which graph the instance runs (<see cref="IRenderGraphPackageFactory.FragmentOf"/>), so no graph
    /// records a grid it was not built for. A change rebuilds beside the installed graph, which presents its last image
    /// until the replacement installs.</summary>
    long Revision { get; }
    /// <summary>Gets this frame's render grid as a fraction of the output on each axis, or one for a package-native
    /// buffer grid. <see cref="FrameAt"/> resolves the pixels the node records with each submission it renders
    /// (<see cref="ShaderPipelineRenderNode.TryGetRenderGrid"/>).</summary>
    double Grid { get; }

    /// <summary>Resolves the allocation ceiling against an image output or independently for an all-buffer graph.</summary>
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
