using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.DirectX.Apis;
using Puck.DirectX.Interfaces;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.Security;
using static Puck.DirectX.DirectXConstants;

namespace Puck.DirectX.Interop;

/// <summary>Who touches an exportable image, which decides its resource flags and resting state.</summary>
public enum DirectXExportableImageAccess {
    /// <summary>This device's work writes it with the usages it declares; the handle crosses to another Direct3D 12 or
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
/// A Direct3D 12 image in <em>shared</em> GPU memory implementing <see cref="IGpuExportableImage"/>: a default-heap
/// texture created by <see cref="DirectXTextures"/> with the shared heap flag, an NT handle to it (from
/// <c>CreateSharedHandle</c>), and a fence to drain the producer's queue. Another backend on the same adapter (a Vulkan
/// host) imports <see cref="SharedHandle"/> and samples the texture without a CPU round-trip.
/// <para>
/// A <see cref="DirectXExportableImageAccess.ComputeWrite"/> texture has the resource flags, initial state and
/// optimized clear value its declared usages need, as a <see cref="DirectXGpuImage"/> does. Both simultaneous-access
/// shapes have fixed flags, and start and rest in <c>COMMON</c>, the cross-device handoff state their foreign device
/// expects; a legacy first UAV use promotes from <c>COMMON</c>. The producer's final recorded barrier returns a written
/// texture to <c>COMMON</c> via <see cref="GpuImageLayout.External"/>, and <see cref="FinalizeForExport"/> only blocks
/// on a fence until that submitted work completes. Single-thread affine.
/// </para>
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXGpuExportableImage : IGpuExportableImage {
    private readonly IDirectXDeviceContext m_deviceContext;
    private readonly GCHandle m_imageViewToken;

    private bool m_disposed;
    private nint m_fence;
    private HANDLE m_fenceEvent;
    private ulong m_fenceValue;
    private nint m_resource;
    private HANDLE m_sharedHandle;

    /// <summary>Initializes a new instance, allocating the shared texture and its fence.</summary>
    /// <param name="request">The device context whose device creates the texture and whose queue the fence drains, the
    /// pixel format, and the image extent in pixels.</param>
    /// <param name="format">The neutral format, which <paramref name="request"/> carries translated; a color
    /// format.</param>
    /// <param name="usage">The usages the image declares: what the caller asked for under
    /// <see cref="DirectXExportableImageAccess.ComputeWrite"/>, and what the fixed flags permit under the
    /// simultaneous-access shapes.</param>
    /// <param name="access">Who writes and who reads the image — see <see cref="DirectXExportableImageAccess"/>.
    /// Direct3D 11 can open the shared handle only under the two simultaneous-access shapes.</param>
    /// <exception cref="ArgumentNullException">The request's device context is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The format is a depth format, or the usage does not fit it.</exception>
    /// <exception cref="DirectXException">A Direct3D 12 call failed.</exception>
    public DirectXGpuExportableImage(DirectXGpuImageRequest request, GpuPixelFormat format, GpuImageUsage usage, DirectXExportableImageAccess access) {
        var (deviceContext, dxgiFormat, width, height) = request;

        ArgumentNullException.ThrowIfNull(deviceContext);
        GpuImageUsages.Validate(
            format: format,
            height: height,
            usage: usage,
            width: width
        );

        if (GpuPixelFormats.IsDepth(format: format)) {
            throw new ArgumentException(
                message: "A depth attachment is never exported.",
                paramName: nameof(format)
            );
        }

        m_deviceContext = deviceContext;
        Format = format;
        Height = height;
        Usage = usage;
        Width = width;

        // The foreign-write shape swaps UAV capability for RENDER_TARGET: its Direct3D 11 writer opens the handle
        // with D3D11-expressible binds, performs any compute work in a private UAV, and copies into this texture.
        // Adding simultaneous access to the compute-write shape lets a Direct3D 11 reader open the same allocation.
        var (flags, clearValue) = (access switch {
            DirectXExportableImageAccess.ForeignWrite => (D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET | D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_SIMULTANEOUS_ACCESS, ((D3D12_CLEAR_VALUE?)null)),
            DirectXExportableImageAccess.ComputeWriteForeignRead => (D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS | D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET | D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_SIMULTANEOUS_ACCESS, ((D3D12_CLEAR_VALUE?)null)),
            _ => DirectXTextures.OfUsage(
                format: dxgiFormat,
                usage: usage
            ),
        });
        var initialState = ((access == DirectXExportableImageAccess.ComputeWrite)
            ? DirectXTextures.InitialStateOf(usage: usage)
            : D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON
        );
        var device = ((ID3D12Device*)deviceContext.Device.Handle);
        var resource = DirectXTextures.CreateCommitted(
            clearValue: clearValue,
            device: device,
            flags: flags,
            format: dxgiFormat,
            heapFlags: D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_SHARED,
            height: height,
            initialState: initialState,
            memory: deviceContext.Memory,
            width: width
        );

        m_resource = ((nint)resource);

        // The resource is counted from here on, so a failure below releases everything made so far, the count
        // included, before it propagates.
        try {
            DirectXResourceStates.Register(
                resource: m_resource,
                state: initialState
            );

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
                Format = dxgiFormat,
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
        } catch {
            ReleaseResources(drainQueue: false);
            throw;
        }
    }

    /// <inheritdoc/>
    public GpuPixelFormat Format { get; }
    /// <inheritdoc/>
    public uint Height { get; }
    /// <inheritdoc/>
    public nint ImageHandle => m_resource;
    /// <inheritdoc/>
    public nint ImageViewHandle => GCHandle.ToIntPtr(value: m_imageViewToken);
    /// <inheritdoc/>
    public nint SharedHandle => m_sharedHandle;
    /// <inheritdoc/>
    public GpuImageUsage Usage { get; }
    /// <inheritdoc/>
    public uint Width { get; }

    // Releases every object the image holds, each one only if it was made, and counts the resource's release.
    private void ReleaseResources(bool drainQueue) {
        DirectXResourceStates.Forget(resource: m_resource);
        DirectXSimultaneousAccessResources.Withdraw(resourceHandle: m_resource);

        if (
            drainQueue &&
            (0 != m_fence)
        ) {
            _ = DirectXCommandCalls.Drain(
                calls: DirectXDeviceCommandCalls.Of(deviceContext: m_deviceContext),
                fence: ((ID3D12Fence*)m_fence),
                fenceEvent: m_fenceEvent,
                fenceValue: ref m_fenceValue,
                queue: ((ID3D12CommandQueue*)m_deviceContext.CommandQueueHandle)
            );
        }

        if (m_imageViewToken.IsAllocated) {
            m_imageViewToken.Free();
        }

        Release(pointer: ref m_fence);
        DirectXDeviceMemory.CountReleased(
            memory: m_deviceContext.Memory,
            resource: m_resource
        );
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
    private void WaitForGpu() =>
        DirectXCommandCalls.SignalAndWait(
            calls: DirectXDeviceCommandCalls.Of(deviceContext: m_deviceContext),
            fence: ((ID3D12Fence*)m_fence),
            fenceEvent: m_fenceEvent,
            fenceValue: ref m_fenceValue,
            queue: ((ID3D12CommandQueue*)m_deviceContext.CommandQueueHandle)
        );

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
    /// <summary>Waits for the producer queue, then releases the texture, its shared handle and its fence. Safe to call
    /// more than once.</summary>
    /// <exception cref="InvalidOperationException">The device context was disposed first, so the device has already
    /// reported the texture as leaked and the owner's teardown order is wrong; the image stays undisposed and its
    /// texture stays counted as held.</exception>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        // The texture and fence are children of the device. An owner that releases this image after its device context
        // is gone has its teardown in the wrong order, and the caller disposing this image is that owner.
        if (!m_deviceContext.IsInitialized) {
            throw new InvalidOperationException(message: $"A {nameof(DirectXGpuExportableImage)} was released after its device context was disposed; the owner disposing it must release it before the device goes.");
        }

        m_disposed = true;
        ReleaseResources(drainQueue: (0 != m_deviceContext.CommandQueueHandle));
    }
}
