namespace Puck.Abstractions.Gpu;

/// <summary>One wait a submission carries on a fence another device signals: the submission starts only once
/// <paramref name="Fence"/> reaches <paramref name="Value"/>. Direct3D 12 issues it as <c>ID3D12CommandQueue::Wait</c>
/// before the submission's lists; Vulkan puts the imported timeline semaphore and the value in the submission's wait
/// list.</summary>
/// <param name="Fence">The shared fence, created on or imported into the submitting device.</param>
/// <param name="Value">The value the producer's write signals; a positive value, since every fence starts at zero.</param>
public readonly record struct GpuExternalWait(IGpuSharedFence Fence, ulong Value);
