using System.Runtime.Versioning;
using Puck.DirectX.Interop;

namespace Puck.DirectX;

/// <summary>
/// Implements <see cref="IGpuSurfaceExportFactory"/> for Direct3D 12 by creating <see cref="DirectXGpuExportableImage"/>
/// instances, converting <see cref="GpuPixelFormat"/> constants to <c>DXGI_FORMAT</c> values. Registered only on the
/// Direct3D 12 backend, which can hand a shared texture to another backend on the same adapter.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed class DirectXGpuSurfaceExportFactory : IGpuSurfaceExportFactory {
    private static DirectXGpuExportableImage Create(IGpuDeviceContext deviceContext, GpuPixelFormat format, uint width, uint height, GpuImageUsage usage, DirectXExportableImageAccess access) =>
        new(
            access: access,
            format: format,
            request: DirectXGpuImageRequest.From(
                deviceContext: deviceContext,
                format: format,
                height: height,
                width: width
            ),
            usage: usage
        );

    /// <inheritdoc/>
    public IGpuExportableImage CreateExportableImage(IGpuDeviceContext deviceContext, GpuPixelFormat format, uint width, uint height, GpuImageUsage usage) =>
        Create(
            access: DirectXExportableImageAccess.ComputeWrite,
            deviceContext: deviceContext,
            format: format,
            height: height,
            usage: usage,
            width: width
        );
    /// <summary>Creates an exportable image whose shared handle another API family can open — with
    /// <c>ALLOW_SIMULTANEOUS_ACCESS</c>, so a Direct3D 11 device can open and write it while this device merely owns
    /// the allocation and samples it. The D3D11 producer writes a private texture and copies into this
    /// render-target-bindable shared allocation. Not part of the neutral interface: the caller is Windows-specific by
    /// construction.
    /// <para>This path supports the GPU-resident zero-copy camera tier: the Windows camera graphs' shared-texture
    /// leaves write into the textures it creates.</para></summary>
    /// <param name="deviceContext">The Direct3D 12 device context that allocates the texture.</param>
    /// <param name="format">The neutral <see cref="GpuPixelFormat"/>.</param>
    /// <param name="width">The image width in pixels.</param>
    /// <param name="height">The image height in pixels.</param>
    /// <returns>The exportable image (its <see cref="IGpuExportableImage.SharedHandle"/> is the cross-API handle),
    /// declaring <see cref="GpuImageUsage.Sampled"/> and <see cref="GpuImageUsage.ColorAttachment"/>.</returns>
    public IGpuExportableImage CreateSimultaneousAccessImage(IGpuDeviceContext deviceContext, GpuPixelFormat format, uint width, uint height) =>
        Create(
            access: DirectXExportableImageAccess.ForeignWrite,
            deviceContext: deviceContext,
            format: format,
            height: height,
            usage: GpuImageUsage.Sampled | GpuImageUsage.ColorAttachment,
            width: width
        );
    /// <summary>Creates an exportable image this device's compute work writes and a Direct3D 11 device can open and
    /// sample — <c>ALLOW_UNORDERED_ACCESS</c> plus <c>ALLOW_SIMULTANEOUS_ACCESS</c>. The reader sees whichever frame last
    /// landed; nothing fences the two devices.</summary>
    /// <param name="deviceContext">The Direct3D 12 device context that allocates the texture.</param>
    /// <param name="format">The neutral <see cref="GpuPixelFormat"/>.</param>
    /// <param name="width">The image width in pixels.</param>
    /// <param name="height">The image height in pixels.</param>
    /// <returns>The exportable image (its <see cref="IGpuExportableImage.SharedHandle"/> is the cross-API handle),
    /// declaring <see cref="GpuImageUsage.Sampled"/>, <see cref="GpuImageUsage.Storage"/> and
    /// <see cref="GpuImageUsage.ColorAttachment"/>.</returns>
    public IGpuExportableImage CreateSharedComputeImage(IGpuDeviceContext deviceContext, GpuPixelFormat format, uint width, uint height) =>
        Create(
            access: DirectXExportableImageAccess.ComputeWriteForeignRead,
            deviceContext: deviceContext,
            format: format,
            height: height,
            usage: GpuImageUsage.Sampled | GpuImageUsage.Storage | GpuImageUsage.ColorAttachment,
            width: width
        );
}
/// <summary>
/// Implements <see cref="IGpuImageFactory"/> for Direct3D 12 by creating <see cref="DirectXGpuImage"/> instances.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed class DirectXGpuImageFactory : IGpuImageFactory {
    /// <inheritdoc/>
    public IGpuImage Create(IGpuDeviceContext deviceContext, GpuPixelFormat format, uint width, uint height, GpuImageUsage usage) =>
        new DirectXGpuImage(
            format: format,
            request: DirectXGpuImageRequest.From(
                deviceContext: deviceContext,
                format: format,
                height: height,
                width: width
            ),
            usage: usage
        );
}
