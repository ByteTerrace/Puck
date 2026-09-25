using Puck.Abstractions.Gpu;

namespace Puck.Shaders;

/// <summary>What a document's <see cref="ShaderPipelineDocumentPassKind"/> implies about the pipeline that runs it and
/// the kind the planner gives it.</summary>
public static class ShaderPipelinePassKinds {
    /// <summary>Returns the shader stages a pass of this kind runs, which its pipeline layout makes every binding
    /// visible to: <see cref="GpuShaderStage.Compute"/> for a compute pass, and <see cref="GpuShaderStage.Vertex"/> with
    /// <see cref="GpuShaderStage.Fragment"/> for a fullscreen or geometry pass. This is the one place a pass kind
    /// becomes stages; a package's pass plans its own pipelines and has no document kind.</summary>
    /// <param name="kind">The pass kind.</param>
    /// <returns>The pass's shader stages.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is not a pass kind.</exception>
    public static GpuShaderStage Stages(this ShaderPipelineDocumentPassKind kind) => kind switch {
        ShaderPipelineDocumentPassKind.Compute => GpuShaderStage.Compute,
        ShaderPipelineDocumentPassKind.Fullscreen or ShaderPipelineDocumentPassKind.Geometry => (GpuShaderStage.Vertex | GpuShaderStage.Fragment),
        _ => throw new ArgumentOutOfRangeException(
            actualValue: kind,
            message: "The value is not a pass kind.",
            paramName: nameof(kind)
        ),
    };
    /// <summary>Returns the planner's kind for a document's pass: the same compute, fullscreen or geometry kind.</summary>
    /// <param name="kind">The document's pass kind.</param>
    /// <returns>The planner's kind.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is not a pass kind.</exception>
    public static ShaderPipelinePassKind PlanKind(this ShaderPipelineDocumentPassKind kind) => kind switch {
        ShaderPipelineDocumentPassKind.Compute => ShaderPipelinePassKind.Compute,
        ShaderPipelineDocumentPassKind.Fullscreen => ShaderPipelinePassKind.Fullscreen,
        ShaderPipelineDocumentPassKind.Geometry => ShaderPipelinePassKind.Geometry,
        _ => throw new ArgumentOutOfRangeException(
            actualValue: kind,
            message: "The value is not a pass kind.",
            paramName: nameof(kind)
        ),
    };
}
