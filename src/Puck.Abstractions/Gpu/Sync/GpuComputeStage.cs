namespace Puck.Abstractions.Gpu;

/// <summary>
/// Specifies a bitmask of pipeline stages bounding a compute barrier's synchronization scope.
/// </summary>
public static class GpuComputeStage {
    /// <summary>The top of the pipe (no prior work to wait on).</summary>
    public const uint TopOfPipe = 0x1;
    /// <summary>The compute shader stage.</summary>
    public const uint ComputeShader = 0x2;
    /// <summary>The fragment/pixel shader stage (a downstream sampler of the produced image).</summary>
    public const uint FragmentShader = 0x4;
    /// <summary>The indirect-argument consumption stage — where the GPU reads an indirect dispatch/draw's arguments
    /// (Vulkan <c>DRAW_INDIRECT</c>; Direct3D 12 <c>EXECUTE_INDIRECT</c>). Use as a barrier's destination stage after a
    /// shader writes an indirect-args buffer it then dispatches from.</summary>
    public const uint DrawIndirect = 0x8;
}
