namespace Puck.Abstractions.Gpu;

/// <summary>One wait a submission carries on a fence another device signals: the submission starts only once
/// <paramref name="Fence"/> reaches <paramref name="Value"/>. Direct3D 12 issues it as <c>ID3D12CommandQueue::Wait</c>
/// before the submission's lists; Vulkan puts the imported timeline semaphore and the value in the submission's wait
/// list. A wait whose fence has already reached its value orders nothing a later submission does not already follow, so
/// a submitter drops it (<see cref="IsReached"/>).</summary>
/// <param name="Fence">The shared fence, created on or imported into the submitting device.</param>
/// <param name="Value">The value the producer's write signals; a positive value, since every fence starts at zero.</param>
public readonly record struct GpuExternalWait(IGpuSharedFence Fence, ulong Value) {
    /// <summary>Gets whether the fence has already reached the value, read without blocking: the producer's write has
    /// completed, so work submitted from now on follows it with no wait on the GPU.</summary>
    /// <exception cref="DeviceLostException">The device was lost.</exception>
    public bool IsReached => (Fence.CompletedValue >= Value);
}
