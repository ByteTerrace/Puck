namespace Puck.Abstractions.Gpu;

/// <summary>
/// Records GPU timestamp commands into a command buffer in a backend-neutral way, mirroring
/// <see cref="IGpuComputeRecorder"/> (all handle-based). The caller brackets work by writing a timestamp before it
/// (top of pipe) and after it (bottom of pipe), then resolves and reads the raw ticks back, converting them with
/// <see cref="GpuTimestampCapabilities.TicksToMilliseconds"/>.
/// </summary>
public interface IGpuTimingRecorder {
    /// <summary>Records a command resetting a range of queries to the unwritten state. Required each frame on Vulkan;
    /// a no-op on Direct3D 12 (its resolve is destructive, so there is nothing to reset).</summary>
    void ResetTimestamps(nint deviceHandle, nint commandBufferHandle, nint poolHandle, uint firstQuery, uint queryCount);
    /// <summary>Records a command writing a timestamp into one query once the given <see cref="GpuTimingStage"/> completes.</summary>
    void WriteTimestamp(nint deviceHandle, nint commandBufferHandle, nint poolHandle, uint queryIndex, GpuTimingStage stageFlags);
    /// <summary>Records a command resolving a range of queries into the pool's readback storage. A no-op on Vulkan
    /// (results are read directly from the pool); on Direct3D 12 it copies the query heap into the readback buffer.</summary>
    void ResolveTimestamps(nint deviceHandle, nint commandBufferHandle, nint poolHandle, uint firstQuery, uint queryCount);
    /// <summary>Reads resolved raw timestamp ticks back into <paramref name="rawTicks"/>. The submit that wrote and
    /// resolved the queries must have completed first.</summary>
    /// <returns>The number of timestamps read (0 when the results are not yet available).</returns>
    uint ReadTimestamps(nint deviceHandle, nint poolHandle, uint firstQuery, uint queryCount, Span<ulong> rawTicks);
}
