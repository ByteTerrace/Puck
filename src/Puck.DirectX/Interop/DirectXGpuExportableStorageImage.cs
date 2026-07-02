using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.DirectX.Interfaces;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.Security;
using Windows.Win32.System.Com;
using static Puck.DirectX.DirectXConstants;

namespace Puck.DirectX.Interop;

/// <summary>
/// A Direct3D 12 compute storage image in <em>shared</em> GPU memory implementing
/// <see cref="IGpuExportableStorageImage"/>. It is the compute-dispatch counterpart of
/// <see cref="DirectXGpuExportableRenderTarget"/>: a DEFAULT-heap texture created with both
/// <c>ALLOW_UNORDERED_ACCESS</c> (a compute shader writes it as a UAV) and the shared heap flag, an NT handle to it
/// (from <c>CreateSharedHandle</c>), and a fence to drain the producer's queue. Another backend on the same adapter
/// (a Vulkan host) imports <see cref="SharedHandle"/> and samples the texture without a CPU round-trip.
/// <para>
/// The texture is created in the <c>UNORDERED_ACCESS</c> state — matching the plain
/// <see cref="DirectXGpuStorageImage"/> and the compute recorder's seeded prior state — so the recorder's
/// per-resource state tracking stays the single source of truth: the producer transitions the texture to
/// <c>COMMON</c> (the cross-API handoff state, via <see cref="GpuImageLayout.External"/>) as its final recorded
/// barrier, and <see cref="FinalizeForExport"/> only blocks on a fence until that submitted work completes.
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
    /// <param name="simultaneousAccess">Whether to create the texture with <c>ALLOW_SIMULTANEOUS_ACCESS</c> (and a
    /// <c>COMMON</c> initial state) — required for Direct3D 11 to open the shared handle (e.g. the camera GPU tier,
    /// where Media Foundation's D3D11 device writes the texture and this device never touches it). The default keeps
    /// the historic D3D12-writes shape byte-identical.</param>
    /// <exception cref="ArgumentNullException"><paramref name="deviceContext"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A dimension is zero.</exception>
    /// <exception cref="DirectXException">A Direct3D 12 call failed.</exception>
    public DirectXGpuExportableStorageImage(IDirectXDeviceContext deviceContext, DXGI_FORMAT format, uint width, uint height, bool simultaneousAccess = false) {
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

        var device = (ID3D12Device*)deviceContext.Device.Handle;
        var heapProperties = new D3D12_HEAP_PROPERTIES {
            Type = D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT,
        };
        // The simultaneous-access (cross-API-writable) variant swaps UAV capability for RENDER_TARGET: its foreign
        // Direct3D 11 writer opens the handle with D3D11-expressible binds (BGRA has no D3D11 UAV, so a UAV-flagged
        // texture is refused with E_INVALIDARG), and this device never dispatches into it. The historic D3D12-writes
        // shape keeps ALLOW_UNORDERED_ACCESS (the compute producer's UAV).
        var textureDesc = new D3D12_RESOURCE_DESC {
            DepthOrArraySize = 1,
            Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_TEXTURE2D,
            Flags = (simultaneousAccess
                ? (D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET | D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_SIMULTANEOUS_ACCESS)
                : D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS),
            Format = format,
            Height = height,
            Layout = D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_UNKNOWN,
            MipLevels = 1,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, },
            Width = width,
        };

        void* resource;
        var resourceIid = ID3D12Resource.IID_Guid;

        // A simultaneous-access texture rests in (and decays to) COMMON — the cross-API handoff state its foreign
        // writer expects; the historic D3D12-writes shape keeps UNORDERED_ACCESS (the compute recorder's seeded state).
        device->CreateCommittedResource(
            in heapProperties,
            D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_SHARED,
            in textureDesc,
            (simultaneousAccess ? D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON : D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_UNORDERED_ACCESS),
            (D3D12_CLEAR_VALUE?)null,
            in resourceIid,
            &resource
        );
        m_resource = (nint)resource;

        var sharedHandle = default(HANDLE);

        device->CreateSharedHandle(
            pObject: (ID3D12DeviceChild*)resource,
            pAttributes: (SECURITY_ATTRIBUTES*)null,
            Access: GenericAll,
            Name: default(PCWSTR),
            pHandle: &sharedHandle
        );
        m_sharedHandle = sharedHandle;

        m_imageViewToken = GCHandle.Alloc(new DirectXImageView {
            Format = format,
            ResourceHandle = m_resource,
        });

        device->CreateFence(
            InitialValue: 0,
            Flags: default,
            riid: ID3D12Fence.IID_Guid,
            ppFence: out var fence
        );
        m_fence = (nint)fence;
        m_fenceValue = 1;
        m_fenceEvent = PInvoke.CreateEvent(
            lpEventAttributes: (SECURITY_ATTRIBUTES*)null,
            bManualReset: false,
            bInitialState: false,
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
    public nint ImageViewHandle => GCHandle.ToIntPtr(m_imageViewToken);
    /// <inheritdoc/>
    public uint Height { get; }
    /// <inheritdoc/>
    public nint SharedHandle => m_sharedHandle;
    /// <inheritdoc/>
    public uint Width { get; }

    private static void Release(ref nint pointer) {
        if (0 != pointer) {
            _ = ((IUnknown*)pointer)->Release();
            pointer = 0;
        }
    }
    private void WaitForGpu() {
        var fence = (ID3D12Fence*)m_fence;
        var value = m_fenceValue;

        ((ID3D12CommandQueue*)m_deviceContext.CommandQueueHandle)->Signal(
            fence,
            value
        );
        m_fenceValue++;

        if (fence->GetCompletedValue() < value) {
            fence->SetEventOnCompletion(
                value,
                m_fenceEvent
            );
            _ = PInvoke.WaitForSingleObject(
                hHandle: m_fenceEvent,
                dwMilliseconds: uint.MaxValue
            );
        }
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

        if (
            (0 != m_deviceContext.CommandQueueHandle) &&
            (0 != m_fence)
        ) {
            WaitForGpu();
        }

        if (m_imageViewToken.IsAllocated) {
            m_imageViewToken.Free();
        }

        Release(ref m_fence);
        Release(ref m_resource);

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
