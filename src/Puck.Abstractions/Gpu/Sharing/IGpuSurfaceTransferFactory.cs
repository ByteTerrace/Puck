using System.Diagnostics.CodeAnalysis;

namespace Puck.Abstractions.Gpu;

/// <summary>
/// Creates backend-neutral surface readback, upload, and import objects, bound to the device context the factory
/// belongs to (<see cref="IGpuDeviceContext.Services"/>), so none of their calls takes a device. Every create call
/// returns a new instance — both backends construct one unconditionally — that the caller owns and disposes when
/// finished, before the device goes; disposing it releases the GPU resources behind any handles or memory it returned.
/// </summary>
public interface IGpuSurfaceTransferFactory {
    /// <summary>Creates a surface readback object; the caller owns and disposes it.</summary>
    IGpuSurfaceReadback CreateReadback();
    /// <summary>Creates a surface upload object; the caller owns and disposes it.</summary>
    IGpuSurfaceUpload CreateUpload();
    /// <summary>Creates a surface import object; the caller owns and disposes it.</summary>
    IGpuSurfaceImport CreateImport();
    /// <summary>Opens a shared fence another device created (a Direct3D 12 shared fence's NT handle) on this device, so
    /// its submissions can wait on it through <see cref="IGpuQueueSubmitter.AddExternalWait"/>. Direct3D 12 opens it
    /// with <c>OpenSharedHandle</c>; Vulkan imports it as a timeline semaphore through
    /// <c>VK_KHR_external_semaphore_win32</c>'s Direct3D 12 fence handle type. The handle stays the caller's.</summary>
    /// <param name="sharedHandle">The fence's shared NT handle.</param>
    /// <param name="fence">When this returns <see langword="true"/>, the opened fence, owned by the caller.</param>
    /// <param name="refusal">When this returns <see langword="false"/>, why the device cannot open it (the extension it
    /// lacks, or the call that failed); empty otherwise.</param>
    /// <returns>Whether the fence was opened.</returns>
    bool TryImportFence(nint sharedHandle, [NotNullWhen(true)] out IGpuSharedFence? fence, out string refusal);
}
