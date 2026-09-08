using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.DirectX.Interfaces;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.Security;
using static Puck.DirectX.DirectXConstants;

namespace Puck.DirectX.Interop;

/// <summary>Who touches an exportable image, which decides its resource flags and resting state.</summary>
public enum DirectXExportableImageAccess {
    /// <summary>This device's compute work writes it through a UAV; the handle crosses to another Direct3D 12 or
    /// Vulkan device.</summary>
    ComputeWrite,
    /// <summary>A foreign Direct3D 11 device writes it (copying from a private texture); this device never
    /// dispatches into it. <c>ALLOW_SIMULTANEOUS_ACCESS</c> with render-target binds, resting in <c>COMMON</c>.</summary>
    ForeignWrite,
    /// <summary>This device's compute work writes it through a UAV while a foreign Direct3D 11 device opens and
    /// samples it: <c>ALLOW_UNORDERED_ACCESS</c> plus <c>ALLOW_SIMULTANEOUS_ACCESS</c>, plus <c>ALLOW_RENDER_TARGET</c>
    /// — without a render-target bind Direct3D 11 refuses to open the allocation. The reader sees whichever frame
    /// last landed.</summary>
    ComputeWriteForeignRead,
}
/// <summary>
/// A Direct3D 12 compute storage image in <em>shared</em> GPU memory implementing
/// <see cref="IGpuExportableStorageImage"/>. It is the compute-dispatch counterpart of
/// <see cref="DirectXGpuExportableRenderTarget"/>: a default-heap texture created with both
/// <c>ALLOW_UNORDERED_ACCESS</c> (a compute shader writes it as a UAV) and the shared heap flag, an NT handle to it
/// (from <c>CreateSharedHandle</c>), and a fence to drain the producer's queue. Another backend on the same adapter
/// (a Vulkan host) imports <see cref="SharedHandle"/> and samples the texture without a CPU round-trip.
/// <para>
/// A private <see cref="DirectXExportableImageAccess.ComputeWrite"/> texture starts in
/// <c>UNORDERED_ACCESS</c>, matching <see cref="DirectXGpuStorageImage"/>. Both simultaneous-access shapes start and
/// rest in <c>COMMON</c>, the immutable enhanced-barrier layout and cross-device handoff state their foreign device
/// expects; a legacy first UAV use promotes from <c>COMMON</c>. The producer's final recorded barrier returns a
/// compute-written texture to <c>COMMON</c> via <see cref="GpuImageLayout.External"/>, and
/// <see cref="FinalizeForExport"/> only blocks on a fence until that submitted work completes.
/// Single-thread affine.
/// </para>
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXGpuExportableStorageImage : IGpuExportableStorageImage {
    private readonly IDirectXDeviceContext m_deviceContext;
    private readonly GCHandle m_imageViewToken;

    private bool m_disposed;
    private nint m_fence;
    private HANDLE m_fenceEvent;
    private ulong m_fenceValue;
    private nint m_resource;
    private HANDLE m_sharedHandle;

    /// <summary>Initializes a new instance, allocating the shared UAV-capable default-heap texture and its fence.</summary>
    /// <param name="deviceContext">The device context whose device creates the texture and whose queue the fence drains.</param>
    /// <param name="format">The pixel format.</param>
    /// <param name="width">The image width in pixels.</param>
    /// <param name="height">The image height in pixels.</param>
    /// <param name="access">Who writes and who reads the image — see <see cref="DirectXExportableImageAccess"/>.
    /// Direct3D 11 can open the shared handle only under the two simultaneous-access shapes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="deviceContext"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A dimension is zero.</exception>
    /// <exception cref="DirectXException">A Direct3D 12 call failed.</exception>
    public DirectXGpuExportableStorageImage(IDirectXDeviceContext deviceContext, DXGI_FORMAT format, uint width, uint height, DirectXExportableImageAccess access = DirectXExportableImageAccess.ComputeWrite) {
        ArgumentNullException.ThrowIfNull(deviceContext);

        if (
            (0 == width) ||
            (0 == height)
        ) {
            throw new ArgumentException(message: "Exportable storage image dimensions must be non-zero.");
        }

        m_deviceContext = deviceContext;
        Height = height;
        Width = width;

        var device = ((ID3D12Device*)deviceContext.Device.Handle);
        var heapProperties = new D3D12_HEAP_PROPERTIES {
            Type = D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT,
        };
        // The foreign-write shape swaps UAV capability for RENDER_TARGET: its Direct3D 11 writer opens the handle
        // with D3D11-expressible binds, performs any compute work in a private UAV, and copies into this texture.
        // The compute-write shapes keep ALLOW_UNORDERED_ACCESS (the compute producer's UAV); adding simultaneous
        // access lets a Direct3D 11 reader open the same allocation.
        var textureDesc = new D3D12_RESOURCE_DESC {
            DepthOrArraySize = 1,
            Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_TEXTURE2D,
            Flags = (access switch {
                DirectXExportableImageAccess.ForeignWrite => (D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET | D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_SIMULTANEOUS_ACCESS),
                DirectXExportableImageAccess.ComputeWriteForeignRead => (D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS | D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET | D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_SIMULTANEOUS_ACCESS),
                _ => D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS,
            }),
            Format = format,
            Height = height,
            Layout = D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_UNKNOWN,
            MipLevels = 1,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, },
            Width = width,
        };

        void* resource;
        var resourceIid = ID3D12Resource.IID_Guid;

        // Both simultaneous-access shapes rest in COMMON — the only enhanced-barrier layout they use and the
        // cross-API handoff state their foreign device expects. A private compute-write texture starts in
        // UNORDERED_ACCESS (the compute recorder's seeded state).
        device->CreateCommittedResource(
            HeapFlags: D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_SHARED,
            InitialResourceState: (UsesCommonInitialState(access: access) ? D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON : D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_UNORDERED_ACCESS),
            pDesc: in textureDesc,
            pHeapProperties: in heapProperties,
            pOptimizedClearValue: ((D3D12_CLEAR_VALUE?)null),
            ppvResource: &resource,
            riidResource: in resourceIid
        );
        m_resource = ((nint)resource);

        if (access != DirectXExportableImageAccess.ComputeWrite) {
            DirectXSimultaneousAccessResources.Register(resourceHandle: m_resource);
        }

        var sharedHandle = default(HANDLE);

        device->CreateSharedHandle(
            Access: GenericAll,
            Name: default(PCWSTR),
            pAttributes: ((SECURITY_ATTRIBUTES*)null),
            pHandle: &sharedHandle,
            pObject: ((ID3D12DeviceChild*)resource)
        );
        m_sharedHandle = sharedHandle;

        m_imageViewToken = GCHandle.Alloc(value: new DirectXImageView {
            Format = format,
            ResourceHandle = m_resource,
        });

        device->CreateFence(
            Flags: default,
            InitialValue: 0,
            ppFence: out var fence,
            riid: ID3D12Fence.IID_Guid
        );
        m_fence = ((nint)fence);
        m_fenceValue = 1;
        m_fenceEvent = PInvoke.CreateEvent(
            bInitialState: false,
            bManualReset: false,
            lpEventAttributes: ((SECURITY_ATTRIBUTES*)null),
            lpName: default(PCWSTR)
        );

        if (m_fenceEvent.IsNull) {
            throw new DirectXException(
                operation: "CreateEventW",
                result: Marshal.GetHRForLastWin32Error()
            );
        }
    }

    /// <inheritdoc/>
    public nint ImageHandle => m_resource;
    /// <inheritdoc/>
    public nint ImageViewHandle => GCHandle.ToIntPtr(value: m_imageViewToken);
    /// <inheritdoc/>
    public uint Height { get; }
    /// <inheritdoc/>
    public nint SharedHandle => m_sharedHandle;
    /// <inheritdoc/>
    public uint Width { get; }

    // Keep the three access shapes' initial-state policy at one allocation-independent door; the constructor uses
    // this result for the native resource state and the documentation above names the same split.
    private static bool UsesCommonInitialState(DirectXExportableImageAccess access) => (access != DirectXExportableImageAccess.ComputeWrite);
    private void WaitForGpu() {
        DirectXFence.SignalAndWait(
            deviceContext: m_deviceContext,
            fenceEvent: m_fenceEvent,
            fenceHandle: m_fence,
            fenceValue: ref m_fenceValue
        );
    }

    /// <inheritdoc/>
    public void FinalizeForExport() {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        // The producer already recorded the COMMON handoff transition and submitted; block on the queue so the
        // importing backend opens the shared handle on completed pixels in the resting state.
        WaitForGpu();
    }
    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        DirectXSimultaneousAccessResources.Withdraw(resourceHandle: m_resource);

        // Drain the producer queue only while the device context is still alive: at host shutdown the DI container
        // may tear the context down before a late owner (e.g. a screen binder's capture feed) releases its shared
        // textures, and CommandQueueHandle THROWS on a disposed context — with the queue gone there is nothing left
        // in flight to wait for, so the drain is skipped rather than resurrected.
        if (
            m_deviceContext.IsInitialized &&
            (0 != m_deviceContext.CommandQueueHandle) &&
            (0 != m_fence)
        ) {
            WaitForGpu();
        }

        if (m_imageViewToken.IsAllocated) {
            m_imageViewToken.Free();
        }

        Release(pointer: ref m_fence);
        Release(pointer: ref m_resource);

        if (!m_sharedHandle.IsNull) {
            _ = PInvoke.CloseHandle(hObject: m_sharedHandle);
            m_sharedHandle = HANDLE.Null;
        }

        if (!m_fenceEvent.IsNull) {
            _ = PInvoke.CloseHandle(hObject: m_fenceEvent);
            m_fenceEvent = HANDLE.Null;
        }
    }
}
