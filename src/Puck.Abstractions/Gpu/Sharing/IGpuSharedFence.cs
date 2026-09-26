namespace Puck.Abstractions.Gpu;

/// <summary>
/// A timeline fence another device or API signals: a Direct3D 12 shared fence, which a Direct3D 11 producer opens and
/// Vulkan imports as a timeline semaphore. Its value only rises. A consumer orders a submission after a producer's write
/// by handing <see cref="IGpuQueueSubmitter.AddExternalWait"/> the value the write signals, so the wait happens on the
/// GPU and neither side blocks. The fence belongs to the device context it was created on or imported into, and is
/// released before that device goes.
/// </summary>
public interface IGpuSharedFence : IDisposable {
    /// <summary>Gets the highest value the fence has reached, read without blocking.</summary>
    /// <exception cref="DeviceLostException">The device was lost.</exception>
    ulong CompletedValue { get; }
}
