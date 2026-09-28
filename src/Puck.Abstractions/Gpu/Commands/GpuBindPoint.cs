namespace Puck.Abstractions.Gpu;

/// <summary>The pipeline a binding applies to: the draws or the dispatches of a command buffer.</summary>
public enum GpuBindPoint {
    /// <summary>The graphics pipeline, read by draws.</summary>
    Graphics,
    /// <summary>The compute pipeline, read by dispatches.</summary>
    Compute,
}
