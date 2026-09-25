using Puck.Abstractions.Gpu;

namespace Puck.Shaders;

/// <summary>What a <see cref="ShaderPipelinePassKind"/> implies about the pipeline that runs it.</summary>
public static class ShaderPipelinePassKinds {
    /// <summary>Returns the shader stages a pass of this kind runs, which its pipeline layout makes every binding
    /// visible to: <see cref="GpuShaderStage.Compute"/> for a compute pass, and <see cref="GpuShaderStage.Vertex"/> with
    /// <see cref="GpuShaderStage.Fragment"/> for a fullscreen or geometry pass. This is the one place a pass kind
    /// becomes stages.</summary>
    /// <param name="kind">The pass kind.</param>
    /// <returns>The pass's shader stages.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is <see cref="ShaderPipelinePassKind.Package"/>,
    /// whose package plans its own pipelines, or not a pass kind.</exception>
    public static GpuShaderStage Stages(this ShaderPipelinePassKind kind) => kind switch {
        ShaderPipelinePassKind.Compute => GpuShaderStage.Compute,
        ShaderPipelinePassKind.Fullscreen or ShaderPipelinePassKind.Geometry => (GpuShaderStage.Vertex | GpuShaderStage.Fragment),
        ShaderPipelinePassKind.Package => throw new ArgumentOutOfRangeException(
            actualValue: kind,
            message: "A package pass's package plans its own pipelines, so the pass kind names no stages.",
            paramName: nameof(kind)
        ),
        _ => throw new ArgumentOutOfRangeException(
            actualValue: kind,
            message: "The value is not a pass kind.",
            paramName: nameof(kind)
        ),
    };
}
