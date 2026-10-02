namespace Puck.Shaders;

/// <summary>A summary of renders completed on the GPU over a span of time: how many, and the one render grid every one
/// of them recorded its passes at (<see cref="ShaderPipelineRenderNode.TryGetRenderGrid"/>). A node keeps one since it
/// was last read (<see cref="ShaderPipelineRenderNode.TakeCompletions"/>), so it states every render completed in that
/// span, not only the newest.</summary>
/// <param name="Renders">The renders completed.</param>
/// <param name="Grid">The render grid every completed render recorded, as a fraction of the output on each axis, or zero
/// when they recorded different grids, when one recorded none, or when no render completed.</param>
public readonly record struct ShaderPipelineCompletions(int Renders, double Grid) {
    /// <summary>Folds later completions into these: the renders add, and the grid stays only while both name the same
    /// one. A summary of no renders leaves the other unchanged.</summary>
    /// <param name="later">The completions that follow these.</param>
    /// <returns>The summary of both spans.</returns>
    public ShaderPipelineCompletions Then(ShaderPipelineCompletions later) => ((later.Renders == 0)
        ? this
        : ((Renders == 0)
            ? later
            : new ShaderPipelineCompletions(Grid: ((Grid == later.Grid) ? Grid : 0d), Renders: (Renders + later.Renders))));
}
