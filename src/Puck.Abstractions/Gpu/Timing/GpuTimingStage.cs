namespace Puck.Abstractions.Gpu;

/// <summary>
/// Specifies the pipeline stage after which a GPU timestamp is written, so frame-start and pass-close timestamps have
/// identical meaning across backends. Vulkan maps these to <c>VK_PIPELINE_STAGE_TOP/BOTTOM_OF_PIPE_BIT</c>; Direct3D
/// 12 ignores them (a timestamp query is a point-in-time <c>EndQuery</c>), so the cross-backend contract is "top of
/// pipe = before any work this mark depends on, bottom of pipe = after all of it".
/// </summary>
[Flags]
public enum GpuTimingStage : uint {
    /// <summary>No stage.</summary>
    None = 0,
    /// <summary>The top of the pipe — written before the work the mark precedes (used for a frame-start mark).</summary>
    TopOfPipe = 0x1,
    /// <summary>The bottom of the pipe — written after all prior work completes (used to close a pass).</summary>
    BottomOfPipe = 0x2,
}
