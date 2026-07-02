namespace Puck.Abstractions.Gpu;

/// <summary>
/// A backend-neutral pool of GPU timestamp queries (a Vulkan <c>VkQueryPool</c>, or a Direct3D 12
/// <c>ID3D12QueryHeap</c> paired with its readback buffer), owned for its lifetime. Passed by handle into
/// <see cref="IGpuTimingRecorder"/> verbs.
/// </summary>
public interface IGpuTimingPool : IDisposable {
    /// <summary>Gets the native pool handle (a <c>VkQueryPool</c>, or a token for the Direct3D 12 query heap + readback buffer).</summary>
    nint PoolHandle { get; }
    /// <summary>Gets the maximum number of timestamp queries the pool holds.</summary>
    uint Capacity { get; }
}
