namespace Puck.Abstractions;

/// <summary>
/// One captured frame handed to a <see cref="ICaptureSink"/>: the pixels plus the diagnostic metadata a tool
/// needs to order and time-align frames.
/// </summary>
/// <param name="Surface">The captured pixels. Sinks consume the CPU-pixel variant; the producing adapter
/// converts a GPU/shared-handle surface to host pixels (via <see cref="IGpuSurfaceReadback"/>) before handing
/// it over.</param>
/// <param name="FrameIndex">The zero-based index of this captured frame within the capture session.</param>
/// <param name="TimestampTicks">The capture time in engine ticks (the deterministic 50400-tick base), for
/// time-aligning captured frames against the simulation clock.</param>
public readonly record struct CaptureFrame(
    Surface Surface,
    long FrameIndex,
    ulong TimestampTicks
);
