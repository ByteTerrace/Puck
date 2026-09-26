using System.Runtime.Versioning;
using Puck.DirectX.Interop;

namespace Puck.DirectX;

/// <summary>
/// Implements <see cref="IGpuSurfaceExportFactory"/> for Direct3D 12 by creating <see cref="DirectXGpuExportableImage"/>
/// instances, converting <see cref="GpuPixelFormat"/> constants to <c>DXGI_FORMAT</c> values. Registered only on the
/// Direct3D 12 backend, which can hand a shared texture to another backend on the same adapter.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed class DirectXGpuSurfaceExportFactory(DirectXDeviceContext deviceContext) : IGpuSurfaceExportFactory {
    private DirectXGpuExportableImage Create(GpuPixelFormat format, uint width, uint height, GpuImageUsage usage, DirectXExportableImageAccess access) =>
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
    public IGpuExportableImage CreateExportableImage(GpuPixelFormat format, uint width, uint height, GpuImageUsage usage) =>
        Create(
            access: DirectXExportableImageAccess.ComputeWrite,
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
    /// <param name="format">The neutral <see cref="GpuPixelFormat"/>.</param>
    /// <param name="width">The image width in pixels.</param>
    /// <param name="height">The image height in pixels.</param>
    /// <returns>The exportable image (its <see cref="IGpuExportableImage.SharedHandle"/> is the cross-API handle),
    /// declaring <see cref="GpuImageUsage.Sampled"/> and <see cref="GpuImageUsage.ColorAttachment"/>.</returns>
    public IGpuExportableImage CreateSimultaneousAccessImage(GpuPixelFormat format, uint width, uint height) =>
        Create(
            access: DirectXExportableImageAccess.ForeignWrite,
            format: format,
            height: height,
            usage: GpuImageUsage.Sampled | GpuImageUsage.ColorAttachment,
            width: width
        );
    /// <summary>Creates an exportable image this device's compute work writes and a Direct3D 11 device can open and
    /// sample — <c>ALLOW_UNORDERED_ACCESS</c> plus <c>ALLOW_SIMULTANEOUS_ACCESS</c>. The reader sees whichever frame last
    /// landed; nothing fences the two devices.</summary>
    /// <param name="format">The neutral <see cref="GpuPixelFormat"/>.</param>
    /// <param name="width">The image width in pixels.</param>
    /// <param name="height">The image height in pixels.</param>
    /// <returns>The exportable image (its <see cref="IGpuExportableImage.SharedHandle"/> is the cross-API handle),
    /// declaring <see cref="GpuImageUsage.Sampled"/>, <see cref="GpuImageUsage.Storage"/> and
    /// <see cref="GpuImageUsage.ColorAttachment"/>.</returns>
    public IGpuExportableImage CreateSharedComputeImage(GpuPixelFormat format, uint width, uint height) =>
        Create(
            access: DirectXExportableImageAccess.ComputeWriteForeignRead,
            format: format,
            height: height,
            usage: GpuImageUsage.Sampled | GpuImageUsage.Storage | GpuImageUsage.ColorAttachment,
            width: width
        );
    /// <summary>Creates a shared fence a producer on another device signals to order its writes into this device's
    /// shared images: a Direct3D 11 device opens its handle and signals it after each write, and a submission here, or
    /// on a Vulkan device that imports the handle, waits for the written value on the GPU. Not part of the neutral
    /// interface: only a Direct3D 12 device creates a fence Direct3D 11 can open.</summary>
    /// <returns>The fence, at zero, owned by the caller.</returns>
    /// <exception cref="System.Runtime.InteropServices.COMException">The device refused the fence or its shared
    /// handle.</exception>
    public unsafe IGpuExportableFence CreateExportableFence() =>
        new DirectXExportableFence(device: ((Windows.Win32.Graphics.Direct3D12.ID3D12Device*)deviceContext.Device.Handle));
}
/// <summary>
/// Implements <see cref="IGpuImageFactory"/> for Direct3D 12 by creating <see cref="DirectXGpuImage"/> instances.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed class DirectXGpuImageFactory(DirectXDeviceContext deviceContext) : IGpuImageFactory {
    /// <inheritdoc/>
    public IGpuImage Create(GpuPixelFormat format, uint width, uint height, GpuImageUsage usage, in GpuObjectName name) {
        GpuImageUsages.ValidateCreate(
            format: format,
            height: height,
            usage: usage,
            width: width
        );

        return Named(
            clearDepth: 1f,
            format: format,
            height: height,
            name: in name,
            usage: usage,
            width: width
        );
    }
    /// <inheritdoc/>
    /// <remarks>The texture is created with the attachment's clear depth as its optimized clear value.</remarks>
    public IGpuImage CreateDepth(in GpuDepthAttachment attachment, uint width, uint height, in GpuObjectName name) {
        GpuImageUsages.ValidateDepth(
            attachment: in attachment,
            height: height,
            width: width
        );

        return Named(
            clearDepth: attachment.ClearDepth,
            format: attachment.Format,
            height: height,
            name: in name,
            usage: GpuImageUsage.DepthAttachment,
            width: width
        );
    }

    // Creates a texture, with the clear depth a depth attachment's optimized clear value takes, and names it.
    private DirectXGpuImage Named(GpuPixelFormat format, uint width, uint height, GpuImageUsage usage, float clearDepth, in GpuObjectName name) {
        var image = new DirectXGpuImage(
            clearDepth: clearDepth,
            format: format,
            request: DirectXGpuImageRequest.From(
                deviceContext: deviceContext,
                format: format,
                height: height,
                width: width
            ),
            usage: usage
        );

        deviceContext.Services.Naming.Name(
            handle: image.ImageHandle,
            kind: GpuObjectKind.Image,
            name: in name
        );

        return image;
    }
}
