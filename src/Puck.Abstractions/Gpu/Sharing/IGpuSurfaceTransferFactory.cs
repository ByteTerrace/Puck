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
    /// <summary>Imports a texture another device allocated in shared memory (a Direct3D 12 shared texture's NT handle)
    /// as an image this device's work writes and hands on to a reader on a third device: the returned image's
    /// <see cref="IGpuExportableImage.SharedHandle"/> is <paramref name="sharedHandle"/>, and its
    /// <see cref="IGpuExportableImage.CompleteWrite"/> signals the imported shared fence behind this device's
    /// submissions, whose handle it reports as <see cref="IGpuExportableImage.SharedFenceHandle"/>, so the reader waits
    /// for the write on its own device. A Vulkan device imports the memory with the usages given and the fence as a
    /// timeline semaphore; a Direct3D 12 device exports its own images and refuses. Both handles stay the caller's and
    /// outlive the image.</summary>
    /// <param name="sharedHandle">The texture's shared NT handle.</param>
    /// <param name="sharedFenceHandle">The shared fence's NT handle, which only this device signals.</param>
    /// <param name="format">The texture's format; a color format.</param>
    /// <param name="width">The texture's width, in pixels.</param>
    /// <param name="height">The texture's height, in pixels.</param>
    /// <param name="usage">The usages this device's work puts the image to.</param>
    /// <param name="image">When this returns <see langword="true"/>, the imported image, owned by the caller.</param>
    /// <param name="refusal">When this returns <see langword="false"/>, why the device cannot import it; empty
    /// otherwise.</param>
    /// <returns>Whether the texture and fence were imported.</returns>
    bool TryImportWritable(nint sharedHandle, nint sharedFenceHandle, GpuPixelFormat format, uint width, uint height, GpuImageUsage usage, [NotNullWhen(true)] out IGpuExportableImage? image, out string refusal);
}
