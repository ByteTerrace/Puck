using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.DirectX.Interfaces;
using Puck.DirectX.Interop;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.System.Com;

namespace Puck.DirectX;

/// <summary>
/// Implements <see cref="IGpuSurfaceTransferFactory"/> for Direct3D 12: the neutral <see cref="GpuSurfaceReadback"/> and
/// <see cref="GpuSurfaceUpload"/> over the device context's services, and the import, each bound to the factory's
/// device context.
/// </summary>
/// <param name="deviceContext">The device context every object the factory creates works on.</param>
[SupportedOSPlatform("windows10.0.10240")]
public sealed class DirectXGpuSurfaceTransferFactory(DirectXDeviceContext deviceContext) : IGpuSurfaceTransferFactory {
    /// <inheritdoc/>
    public IGpuSurfaceReadback CreateReadback() =>
        new GpuSurfaceReadback(
            device: () => deviceContext.Device,
            deviceContext: deviceContext
        );
    /// <inheritdoc/>
    public IGpuSurfaceUpload CreateUpload() =>
        new GpuSurfaceUpload(
            device: () => deviceContext.Device,
            deviceContext: deviceContext
        );
    /// <inheritdoc/>
    public IGpuSurfaceImport CreateImport() =>
        new DirectXGpuSurfaceImport(deviceContext: deviceContext);
    /// <inheritdoc/>
    /// <remarks>Opens the handle with <c>ID3D12Device::OpenSharedHandle</c> as a <see cref="DirectXSharedFence"/>; a
    /// refusal names the failed call's result.</remarks>
    public unsafe bool TryImportFence(nint sharedHandle, [NotNullWhen(true)] out IGpuSharedFence? fence, out string refusal) {
        try {
            fence = DirectXSharedFence.Open(
                device: ((ID3D12Device*)deviceContext.Device.Handle),
                sharedHandle: sharedHandle
            );
            refusal = "";

            return true;
        } catch (COMException exception) {
            fence = null;
            refusal = $"ID3D12Device::OpenSharedHandle refused the fence (0x{exception.HResult:X8})";

            return false;
        }
    }
    /// <inheritdoc/>
    /// <remarks>Always refuses: a Direct3D 12 device writes the images it exports itself
    /// (<see cref="DirectXGpuSurfaceExportFactory.CreateSharedComputeImage"/>).</remarks>
    public bool TryImportWritable(nint sharedHandle, nint sharedFenceHandle, GpuPixelFormat format, uint width, uint height, GpuImageUsage usage, [NotNullWhen(true)] out IGpuExportableImage? image, out string refusal) {
        image = null;
        refusal = "a Direct3D 12 device exports the images it writes and imports none to write";

        return false;
    }
}

[SupportedOSPlatform("windows10.0.10240")]
file sealed unsafe class DirectXGpuSurfaceImport(IDirectXDeviceContext deviceContext) : IGpuSurfaceImport {
    // Cache the opened resource + view handle by shared handle. A producer hands over the SAME handle every frame
    // (the exportable texture is stable), so without this each call would OpenSharedHandle again and leak an
    // ID3D12Resource per frame. Mirrors VulkanSurfaceImport's idempotent caching.
    private readonly Dictionary<nint, (nint Resource, nint View)> m_imports = [];

    private bool m_disposed;
    // The device the opened resources live on, from the first import (GpuDeviceOwnership).
    private DirectXDevice? m_heldDevice;

    public GpuImportedSurface Import(
        nint sharedHandle,
        GpuPixelFormat format,
        uint width,
        uint height
    ) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        // Before the cache: a cached resource belongs to the device it was opened on.
        var offered = deviceContext.Device;

        GpuDeviceOwnership.ThrowIfOtherDevice(
            held: m_heldDevice,
            holder: nameof(DirectXGpuSurfaceImport),
            offered: offered
        );
        m_heldDevice = offered;

        if (m_imports.TryGetValue(
            key: sharedHandle,
            value: out var cached
        )) {
            return new GpuImportedSurface(
                ImageHandle: cached.Resource,
                ImageViewHandle: cached.View
            );
        }

        var device = ((ID3D12Device*)offered.Handle);

        void* resource;
        var resourceIid = ID3D12Resource.IID_Guid;

        device->OpenSharedHandle(
            NTHandle: new Windows.Win32.Foundation.HANDLE(value: ((void*)sharedHandle)),
            riid: &resourceIid,
            ppvObj: &resource
        );
        DirectXDeviceMemory.CountAllocated(
            device: device,
            memory: deviceContext.Memory,
            resource: ((ID3D12Resource*)resource),
            role: GpuMemoryRole.DeviceLocal
        );

        var imageView = new DirectXImageView {
            Format = DirectXGpuFormats.ToDxgiFormat(gpuPixelFormat: format),
            ResourceHandle = ((nint)resource),
        };

        var view = DirectXImageViews.Register(view: imageView);

        m_imports[sharedHandle] = (((nint)resource), view);

        return new GpuImportedSurface(
            ImageHandle: ((nint)resource),
            ImageViewHandle: view
        );
    }
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        // A refused release changes nothing: the opened resources stay counted on their device, whose teardown names
        // them.
        if (m_heldDevice is not null) {
            GpuDeviceOwnership.ThrowIfDestroyed(
                held: m_heldDevice,
                holder: nameof(DirectXGpuSurfaceImport)
            );
        }

        m_disposed = true;

        foreach (var (resource, view) in m_imports.Values) {
            if (0 != resource) {
                DirectXDeviceMemory.CountReleased(
                    memory: deviceContext.Memory,
                    resource: resource
                );
                _ = ((IUnknown*)resource)->Release();
            }

            DirectXImageViews.Release(handle: view);
        }

        m_imports.Clear();
    }
}
