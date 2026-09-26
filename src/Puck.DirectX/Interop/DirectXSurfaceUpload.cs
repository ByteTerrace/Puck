using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.DirectX.Apis;
using Puck.DirectX.Interfaces;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.Security;
using static Puck.DirectX.DirectXConstants;

namespace Puck.DirectX.Interop;

/// <summary>
/// Materializes a tightly packed mip chain of CPU pixels, or of 4x4 blocks for a block-compressed format, into a
/// shader-resource-view-sampled Direct3D 12 texture, against a shared <see cref="IDirectXDeviceContext"/>. It owns the
/// default-heap texture, an upload-heap staging buffer laid out by the device's copyable footprints, a shader-visible
/// SRV descriptor heap, and the command resources to drive the copy, rebuilding them when the extent, format or level
/// count changes. Each <see cref="Upload"/> copies every level into the texture and leaves it in both shader-resource
/// states, then exposes the descriptor heap and GPU handle a textured
/// pipeline binds. This is the Direct3D 12 peer of <c>VulkanSurfaceUpload</c> — the consumer/ingest half that
/// lets a DirectX host sample a surface that arrived as host memory. Single-thread affine.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXSurfaceUpload : IDisposable {
    private readonly IDirectXDeviceContext m_deviceContext;
    // The device every object below lives on, from construction (DirectXDeviceOwnership).
    private readonly DirectXDevice m_heldDevice;

    private nint m_commandAllocator;
    private nint m_commandList;
    private bool m_disposed;
    private nint m_fence;
    private HANDLE m_fenceEvent;
    private ulong m_fenceValue;
    private DXGI_FORMAT m_format;
    private ulong m_gpuDescriptorPointer;
    private uint m_height;
    // Each level's placement in the staging buffer, its row count (block rows for a block-compressed format) and its
    // tightly packed row size, as GetCopyableFootprints reports them for the texture.
    private D3D12_PLACED_SUBRESOURCE_FOOTPRINT[] m_layouts = [];
    private uint m_levels;
    private uint[] m_rowCounts = [];
    private ulong[] m_rowSizes = [];
    private nint m_srvHeap;
    private nint m_texture;
    private D3D12_RESOURCE_STATES m_textureState;
    private nint m_uploadBuffer;
    private uint m_width;

    /// <summary>Initializes a new instance of the <see cref="DirectXSurfaceUpload"/> class.</summary>
    /// <param name="deviceContext">The shared device context whose device and queue the upload runs on.</param>
    /// <exception cref="ArgumentNullException"><paramref name="deviceContext"/> is <see langword="null"/>.</exception>
    /// <exception cref="DirectXException">A Direct3D 12 call failed.</exception>
    public DirectXSurfaceUpload(IDirectXDeviceContext deviceContext) {
        ArgumentNullException.ThrowIfNull(deviceContext);

        m_deviceContext = deviceContext;
        m_heldDevice = deviceContext.Device;

        var device = ((ID3D12Device*)m_heldDevice.Handle);

        var calls = DirectXDeviceCommandCalls.Of(deviceContext: deviceContext);
        var commandList = DirectXCommandCalls.CreateCommandList(
            allocator: out var commandAllocator,
            calls: calls,
            type: D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT
        );

        m_commandAllocator = ((nint)commandAllocator);
        m_commandList = ((nint)commandList);
        DirectXCommandCalls.Close(
            calls: calls,
            commandList: commandList
        );

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

    /// <summary>Gets the native <c>ID3D12DescriptorHeap</c> handle to bind via <c>SetDescriptorHeaps</c>.</summary>
    public nint DescriptorHeapHandle => m_srvHeap;
    /// <summary>Gets the GPU descriptor handle (<c>D3D12_GPU_DESCRIPTOR_HANDLE.ptr</c>) of the texture's SRV.</summary>
    public ulong GpuDescriptorPointer => m_gpuDescriptorPointer;
    /// <summary>Gets the native <c>ID3D12Resource</c> handle of the uploaded texture, or zero before the first <see cref="Upload"/>.</summary>
    public nint TextureHandle => m_texture;
    /// <summary>Gets the <c>DXGI_FORMAT</c> the texture was last uploaded as.</summary>
    public DXGI_FORMAT TextureFormat => m_format;

    /// <summary>Copies an image's levels into the SRV texture and leaves it sampleable by every shader stage.</summary>
    /// <param name="pixels">The image's levels from level 0, tightly packed and back to back
    /// (<see cref="GpuPixelFormats.ChainByteLength"/>): rows of texels, or rows of 4x4 blocks for a block-compressed
    /// format.</param>
    /// <param name="format">The pixel format.</param>
    /// <param name="width">The width of level 0, in texels.</param>
    /// <param name="height">The height of level 0, in texels.</param>
    /// <param name="levels">The number of mip levels <paramref name="pixels"/> holds.</param>
    /// <exception cref="ObjectDisposedException">The instance has been disposed.</exception>
    /// <exception cref="ArgumentException"><paramref name="pixels"/> is not exactly the chain's length.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A dimension or the level count is zero, or the level count
    /// exceeds the extent's full chain.</exception>
    /// <exception cref="NotSupportedException">The device cannot sample a two-dimensional texture of
    /// <paramref name="format"/>.</exception>
    /// <exception cref="InvalidOperationException">The context's device is not the one this upload was created on: its owner
    /// did not release it on a device loss.</exception>
    /// <exception cref="DirectXException">A Direct3D 12 call failed.</exception>
    public void Upload(ReadOnlySpan<byte> pixels, GpuPixelFormat format, uint width, uint height, uint levels = 1U) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );
        DirectXDeviceOwnership.ThrowIfOtherDevice(
            held: m_heldDevice,
            holder: nameof(DirectXSurfaceUpload),
            offered: m_deviceContext.Device
        );
        _ = GpuPixelFormats.RequireChain(
            byteLength: pixels.Length,
            format: format,
            height: height,
            levels: levels,
            width: width
        );

        EnsureResources(
            format: format,
            height: height,
            levels: levels,
            width: width
        );
        WriteUploadBuffer(pixels: pixels);

        var calls = DirectXDeviceCommandCalls.Of(deviceContext: m_deviceContext);
        var commandList = ((ID3D12GraphicsCommandList*)m_commandList);
        var allocator = ((ID3D12CommandAllocator*)m_commandAllocator);
        var texture = ((ID3D12Resource*)m_texture);

        DirectXCommandCalls.Reset(
            allocator: allocator,
            calls: calls,
            commandList: commandList
        );

        if (D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST != m_textureState) {
            var toCopyDestination = DirectXBarriers.Transition(
                after: D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST,
                before: m_textureState,
                resource: texture
            );

            commandList->ResourceBarrier(
                NumBarriers: 1,
                pBarriers: &toCopyDestination
            );
        }

        for (var level = 0U; (level < m_levels); level++) {
            var destinationLocation = new D3D12_TEXTURE_COPY_LOCATION {
                Type = D3D12_TEXTURE_COPY_TYPE.D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX,
                pResource = texture,
            };

            destinationLocation.Anonymous.SubresourceIndex = level;

            var sourceLocation = new D3D12_TEXTURE_COPY_LOCATION {
                Type = D3D12_TEXTURE_COPY_TYPE.D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT,
                pResource = ((ID3D12Resource*)m_uploadBuffer),
            };

            sourceLocation.Anonymous.PlacedFootprint = m_layouts[level];
            commandList->CopyTextureRegion(
                DstX: 0,
                DstY: 0,
                DstZ: 0,
                pDst: in destinationLocation,
                pSrc: in sourceLocation,
                pSrcBox: ((D3D12_BOX?)null)
            );
        }

        var toShaderResource = DirectXBarriers.Transition(
            after: DirectXResourceStates.ShaderRead,
            before: D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST,
            resource: texture
        );

        commandList->ResourceBarrier(
            NumBarriers: 1,
            pBarriers: &toShaderResource
        );
        DirectXCommandCalls.Close(
            calls: calls,
            commandList: commandList
        );
        m_textureState = DirectXResourceStates.ShaderRead;

        var executable = ((ID3D12CommandList*)commandList);

        ((ID3D12CommandQueue*)m_deviceContext.CommandQueueHandle)->ExecuteCommandLists(
            NumCommandLists: 1,
            ppCommandLists: &executable
        );
        WaitForGpu();
    }

    // Refuses a format the device cannot sample from a two-dimensional texture, naming the format and what the device
    // reports for it. Every Direct3D 12 device at feature level 11_0 samples BC1 to BC7; the query still answers for the
    // device in hand.
    private static void RequireSampled(ID3D12Device* device, GpuPixelFormat format, DXGI_FORMAT dxgiFormat) {
        const D3D12_FORMAT_SUPPORT1 Required = D3D12_FORMAT_SUPPORT1.D3D12_FORMAT_SUPPORT1_TEXTURE2D | D3D12_FORMAT_SUPPORT1.D3D12_FORMAT_SUPPORT1_SHADER_SAMPLE;

        var support = new D3D12_FEATURE_DATA_FORMAT_SUPPORT {
            Format = dxgiFormat,
        };

        if (
            !DirectXFeatureReads.TryCheck(
                data: ref support,
                feature: D3D12_FEATURE.D3D12_FEATURE_FORMAT_SUPPORT,
                support: new DirectXDeviceFeatureSupport(device: device)
            ) ||
            ((support.Support1 & Required) != Required)
        ) {
            throw new NotSupportedException(message: $"The Direct3D 12 device cannot sample {format} textures: it reports {support.Support1} for {dxgiFormat}.");
        }
    }
    private void EnsureResources(GpuPixelFormat format, uint width, uint height, uint levels) {
        var dxgiFormat = DirectXGpuFormats.ToDxgiFormat(gpuPixelFormat: format);

        if (
            (0 != m_texture) &&
            (m_width == width) &&
            (m_height == height) &&
            (m_format == dxgiFormat) &&
            (m_levels == levels)
        ) {
            return;
        }

        var device = ((ID3D12Device*)m_heldDevice.Handle);

        RequireSampled(
            device: device,
            dxgiFormat: dxgiFormat,
            format: format
        );

        // A resize or format change releases resources the GPU may still be reading: the upload path submits and
        // returns without draining, so in-flight work can outlive the old texture. Drain first, exactly as Dispose
        // does. Skipped on the first allocation, where nothing has been submitted against these resources yet.
        if (0 != m_texture) {
            WaitForGpu();
        }

        DisposeImageResources();

        m_texture = ((nint)DirectXTextures.CreateCommitted(
            device: device,
            format: dxgiFormat,
            height: height,
            initialState: D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST,
            memory: m_deviceContext.Memory,
            mipLevels: checked((ushort)levels),
            width: width
        ));
        m_textureState = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST;

        var description = DirectXTextures.Describe(
            format: dxgiFormat,
            height: height,
            mipLevels: checked((ushort)levels),
            width: width
        );
        var layouts = new D3D12_PLACED_SUBRESOURCE_FOOTPRINT[levels];
        var rowCounts = new uint[levels];
        var rowSizes = new ulong[levels];
        var uploadBytes = 0UL;

        fixed (D3D12_PLACED_SUBRESOURCE_FOOTPRINT* layoutPointer = layouts)
        fixed (uint* rowCountPointer = rowCounts)
        fixed (ulong* rowSizePointer = rowSizes) {
            device->GetCopyableFootprints(
                BaseOffset: 0UL,
                FirstSubresource: 0U,
                NumSubresources: levels,
                pLayouts: layoutPointer,
                pNumRows: rowCountPointer,
                pResourceDesc: &description,
                pRowSizeInBytes: rowSizePointer,
                pTotalBytes: &uploadBytes
            );
        }

        m_layouts = layouts;
        m_rowCounts = rowCounts;
        m_rowSizes = rowSizes;
        m_uploadBuffer = ((nint)DirectXBuffers.CreateCommitted(
            device: device,
            heapType: D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_UPLOAD,
            initialState: D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_GENERIC_READ,
            sizeBytes: uploadBytes
        ));

        if (0 == m_srvHeap) {
            var srvHeap = DirectXDescriptorHeaps.Create(
                count: 1,
                device: device,
                shaderVisible: true,
                type: D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV
            );

            m_srvHeap = ((nint)srvHeap);
            m_gpuDescriptorPointer = GetGpuHeapStart(heap: srvHeap).ptr;
        }

        var srvDesc = new D3D12_SHADER_RESOURCE_VIEW_DESC {
            Format = dxgiFormat,
            Shader4ComponentMapping = DefaultShader4ComponentMapping,
            ViewDimension = D3D12_SRV_DIMENSION.D3D12_SRV_DIMENSION_TEXTURE2D,
        };

        srvDesc.Anonymous.Texture2D = new D3D12_TEX2D_SRV {
            MipLevels = levels,
            MostDetailedMip = 0,
            PlaneSlice = 0,
            ResourceMinLODClamp = 0f,
        };

        device->CreateShaderResourceView(
            pResource: ((ID3D12Resource*)m_texture),
            pDesc: &srvDesc,
            DestDescriptor: GetCpuHeapStart(heap: ((ID3D12DescriptorHeap*)m_srvHeap))
        );

        m_format = dxgiFormat;
        m_height = height;
        m_levels = levels;
        m_width = width;
    }
    // Writes each level's rows, tightly packed and back to back in the source, at its footprint's offset and row pitch.
    private void WriteUploadBuffer(ReadOnlySpan<byte> pixels) {
        var uploadBuffer = ((ID3D12Resource*)m_uploadBuffer);
        var mapped = ((byte*)DirectXCommandCalls.Map(
            calls: DirectXDeviceCommandCalls.Of(deviceContext: m_deviceContext),
            resource: uploadBuffer
        ));

        try {
            var source = 0;

            for (var level = 0; (level < m_layouts.Length); level++) {
                var layout = m_layouts[level];
                var rowBytes = checked((int)m_rowSizes[level]);

                for (var row = 0U; (row < m_rowCounts[level]); row++) {
                    pixels
                        .Slice(
                        length: rowBytes,
                        start: source
                    )
                        .CopyTo(destination: new Span<byte>(
                        length: rowBytes,
                        pointer: ((mapped + layout.Offset) + (((ulong)row) * layout.Footprint.RowPitch))
                    ));
                    source += rowBytes;
                }
            }
        } finally {
            uploadBuffer->Unmap(
                Subresource: 0,
                pWrittenRange: ((D3D12_RANGE*)null)
            );
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
    private void DisposeImageResources() {
        Release(pointer: ref m_uploadBuffer);
        DirectXDeviceMemory.CountReleased(
            memory: m_deviceContext.Memory,
            resource: m_texture
        );
        Release(pointer: ref m_texture);
    }

    /// <summary>Drains the queue, then releases the texture, upload buffer, SRV heap, and command resources. A removed
    /// device counts as drained (<see cref="DirectXCommandCalls.Drain"/>). Safe to call more than once.</summary>
    /// <exception cref="InvalidOperationException">The device went first, disposed with its context or replaced on a loss, so
    /// it has already reported these resources as leaked and the owner's teardown order is wrong.</exception>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        // Every object below is a child of the device, and the device's teardown reports each one still alive as a
        // leak. An owner that releases this upload after its device is gone, disposed with its context or replaced on a
        // loss, has its teardown in the wrong order, and the caller disposing this upload is that owner. A refused
        // release changes nothing: the texture stays counted as held on its device, whose teardown names it.
        DirectXDeviceOwnership.ThrowIfDestroyed(
            held: m_heldDevice,
            holder: nameof(DirectXSurfaceUpload)
        );

        m_disposed = true;

        if (0 != m_fence) {
            _ = DirectXCommandCalls.Drain(
                calls: DirectXDeviceCommandCalls.Of(deviceContext: m_deviceContext),
                fence: ((ID3D12Fence*)m_fence),
                fenceEvent: m_fenceEvent,
                fenceValue: ref m_fenceValue,
                queue: ((ID3D12CommandQueue*)m_deviceContext.CommandQueueHandle)
            );
        }

        DisposeImageResources();
        Release(pointer: ref m_srvHeap);
        Release(pointer: ref m_fence);
        Release(pointer: ref m_commandList);
        Release(pointer: ref m_commandAllocator);

        if (!m_fenceEvent.IsNull) {
            _ = PInvoke.CloseHandle(hObject: m_fenceEvent);
            m_fenceEvent = HANDLE.Null;
        }
    }
}
