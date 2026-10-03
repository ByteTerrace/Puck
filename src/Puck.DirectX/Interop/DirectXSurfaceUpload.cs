using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.DirectX.Apis;
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
/// Materializes a tightly packed mip chain of CPU pixels, or of 4x4 blocks for a block-compressed format, into a
/// shader-resource-view-sampled Direct3D 12 texture, against a shared <see cref="IDirectXDeviceContext"/>. It owns the
/// default-heap texture, an upload-heap staging buffer laid out by the device's copyable footprints, and the command
/// resources to drive the copy, rebuilding the texture and buffer when the extent, format or level count changes. Each
/// <see cref="Upload"/> copies every level into the texture and leaves it in both shader-resource states; a consumer
/// binds <see cref="TextureHandle"/> through a set of its own pool, a range of the device's shader-visible heaps, so the
/// upload holds no descriptor. This is the Direct3D 12 peer of <c>VulkanSurfaceUpload</c> — the consumer/ingest half that
/// lets a DirectX host sample a surface that arrived as host memory. A recording that fails is discarded: the next
/// <see cref="Upload"/> replaces the command allocator and list, so one failed upload never wedges the instance. A
/// rebuild creates the replacement texture and buffer before it retires the current ones, so a refused creation leaves
/// the current texture and its view in place.
/// Single-thread affine.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXSurfaceUpload : IGpuSurfaceUpload {
    // The calls every command, fence and creation below asks; the device's own unless a law supplies others.
    private readonly IDirectXCommandCalls m_calls;
    private readonly IDirectXDeviceContext m_deviceContext;
    // The device every object below lives on, from construction (DirectXDeviceOwnership).
    private readonly DirectXDevice m_heldDevice;

    private nint m_commandAllocator;
    private nint m_commandList;
    // True from the moment a recording begins until its list closes: a list that is open, or whose close failed, is never
    // closed again, so the next upload replaces the allocator and the list before it records.
    private bool m_commandsFaulted;
    private bool m_disposed;
    private nint m_fence;
    private HANDLE m_fenceEvent;
    private ulong m_fenceValue;
    private DXGI_FORMAT m_format;
    private uint m_height;
    private nint m_imageViewHandle;
    // Each level's placement in the staging buffer, its row count (block rows for a block-compressed format) and its
    // tightly packed row size, as GetCopyableFootprints reports them for the texture.
    private D3D12_PLACED_SUBRESOURCE_FOOTPRINT[] m_layouts = [];
    private uint m_levels;
    private uint[] m_rowCounts = [];
    private ulong[] m_rowSizes = [];
    private nint m_texture;
    private D3D12_RESOURCE_STATES m_textureState;
    private nint m_uploadBuffer;
    private uint m_width;

    /// <summary>Initializes a new instance of the <see cref="DirectXSurfaceUpload"/> class.</summary>
    /// <param name="deviceContext">The shared device context whose device and queue the upload runs on.</param>
    /// <exception cref="ArgumentNullException"><paramref name="deviceContext"/> is <see langword="null"/>.</exception>
    /// <exception cref="DirectXException">A Direct3D 12 call failed.</exception>
    public DirectXSurfaceUpload(IDirectXDeviceContext deviceContext) :
        this(
            calls: DirectXDeviceCommandCalls.Of(deviceContext: deviceContext),
            deviceContext: deviceContext
        ) { }
    /// <summary>Initializes a new instance of the <see cref="DirectXSurfaceUpload"/> class whose commands, fence waits and
    /// texture creations are answered by <paramref name="calls"/>, which a law gives a device's calls that fail on
    /// demand.</summary>
    /// <param name="deviceContext">The shared device context whose device and queue the upload runs on.</param>
    /// <param name="calls">The answerer of the command-list, mapping, fence and texture-creation calls, over the device
    /// of <paramref name="deviceContext"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="deviceContext"/> or <paramref name="calls"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="DirectXException">A Direct3D 12 call failed.</exception>
    public DirectXSurfaceUpload(IDirectXDeviceContext deviceContext, IDirectXCommandCalls calls) {
        ArgumentNullException.ThrowIfNull(deviceContext);
        ArgumentNullException.ThrowIfNull(calls);

        m_calls = calls;
        m_deviceContext = deviceContext;
        m_heldDevice = deviceContext.Device;

        var device = ((ID3D12Device*)m_heldDevice.Handle);

        CreateCommandResources();
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

    /// <summary>Gets the native <c>ID3D12Resource</c> handle of the uploaded texture, or zero before the first <see cref="Upload"/>.</summary>
    public nint TextureHandle => m_texture;
    /// <summary>Gets the <c>DXGI_FORMAT</c> the texture was last uploaded as.</summary>
    public DXGI_FORMAT TextureFormat => m_format;

    nint IGpuSurfaceUpload.Upload(ReadOnlyMemory<byte> pixels, GpuPixelFormat format, uint width, uint height, uint levels) {
        Upload(pixels: pixels.Span, format: format, width: width, height: height, levels: levels);
        return m_imageViewHandle;
    }

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
    /// <exception cref="ArgumentOutOfRangeException">A dimension or the level count is zero, a dimension exceeds
    /// Direct3D 12's two-dimensional texture limit (`D3D12_REQ_TEXTURE2D_U_OR_V_DIMENSION`, refused before any
    /// resource is touched, so the current texture and its view stay), or the level count exceeds the extent's full
    /// chain.</exception>
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
        RequireWithinTextureLimit(
            dimension: width,
            name: nameof(width)
        );
        RequireWithinTextureLimit(
            dimension: height,
            name: nameof(height)
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

        if (m_commandsFaulted) {
            CreateCommandResources();
        }

        var commandList = ((ID3D12GraphicsCommandList*)m_commandList);
        var allocator = ((ID3D12CommandAllocator*)m_commandAllocator);
        var texture = ((ID3D12Resource*)m_texture);

        m_commandsFaulted = true;
        DirectXCommandCalls.Reset(
            allocator: allocator,
            calls: m_calls,
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
            calls: m_calls,
            commandList: commandList
        );
        m_commandsFaulted = false;
        m_textureState = DirectXResourceStates.ShaderRead;

        var executable = ((ID3D12CommandList*)commandList);

        ((ID3D12CommandQueue*)m_deviceContext.CommandQueueHandle)->ExecuteCommandLists(
            NumCommandLists: 1,
            ppCommandLists: &executable
        );
        WaitForGpu();
    }

    // Refuses a width or height beyond the largest two-dimensional texture Direct3D 12 admits. The device accepts a larger
    // texture's creation and refuses only the recording that copies into it, so the limit is checked first.
    private static void RequireWithinTextureLimit(uint dimension, string name) {
        const uint Limit = PInvoke.D3D12_REQ_TEXTURE2D_U_OR_V_DIMENSION;

        if (dimension > Limit) {
            throw new ArgumentOutOfRangeException(
                actualValue: dimension,
                message: $"The {name} is {dimension} texels; Direct3D 12 limits a two-dimensional texture to {Limit} texels per side.",
                paramName: name
            );
        }
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

        // The replacement is built whole before anything is swapped, so a creation that fails leaves the current
        // texture, its buffer and its view as they were.
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

        var texture = DirectXTextures.CreateCommitted(
            calls: m_calls,
            device: device,
            format: dxgiFormat,
            height: height,
            initialState: D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST,
            memory: m_deviceContext.Memory,
            mipLevels: checked((ushort)levels),
            width: width
        );
        ID3D12Resource* uploadBuffer;

        try {
            uploadBuffer = DirectXBuffers.CreateCommitted(
                calls: m_calls,
                device: device,
                heapType: D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_UPLOAD,
                initialState: D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_GENERIC_READ,
                sizeBytes: uploadBytes
            );
        } catch {
            DirectXDeviceMemory.CountReleased(
                memory: m_deviceContext.Memory,
                resource: ((nint)texture)
            );
            _ = ((IUnknown*)texture)->Release();

            throw;
        }

        DisposeImageResources();
        m_texture = ((nint)texture);
        m_uploadBuffer = ((nint)uploadBuffer);
        m_textureState = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST;
        m_imageViewHandle = DirectXImageViews.Register(view: new DirectXImageView {
            Format = dxgiFormat,
            ResourceHandle = m_texture,
        });
        m_layouts = layouts;
        m_rowCounts = rowCounts;
        m_rowSizes = rowSizes;
        m_format = dxgiFormat;
        m_height = height;
        m_levels = levels;
        m_width = width;
    }
    // Creates the command allocator and the closed list that records into it, then releases the pair they replace: the
    // first pair at construction, and the pair a failed recording left open. Nothing submitted has used the old pair,
    // since every earlier upload waited for the queue and a failed recording never reached it.
    private void CreateCommandResources() {
        var commandList = DirectXCommandCalls.CreateCommandList(
            allocator: out var commandAllocator,
            calls: m_calls,
            type: D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT
        );

        try {
            DirectXCommandCalls.Close(
                calls: m_calls,
                commandList: commandList
            );
        } catch {
            _ = ((IUnknown*)commandList)->Release();
            _ = ((IUnknown*)commandAllocator)->Release();

            throw;
        }

        Release(pointer: ref m_commandList);
        Release(pointer: ref m_commandAllocator);
        m_commandAllocator = ((nint)commandAllocator);
        m_commandList = ((nint)commandList);
        m_commandsFaulted = false;
    }
    // Writes each level's rows, tightly packed and back to back in the source, at its footprint's offset and row pitch.
    private void WriteUploadBuffer(ReadOnlySpan<byte> pixels) {
        var uploadBuffer = ((ID3D12Resource*)m_uploadBuffer);
        var mapped = ((byte*)DirectXCommandCalls.Map(
            calls: m_calls,
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
            calls: m_calls,
            fence: ((ID3D12Fence*)m_fence),
            fenceEvent: m_fenceEvent,
            fenceValue: ref m_fenceValue,
            queue: ((ID3D12CommandQueue*)m_deviceContext.CommandQueueHandle)
        );
    private void DisposeImageResources() {
        DirectXImageViews.Release(handle: m_imageViewHandle);
        m_imageViewHandle = 0;
        Release(pointer: ref m_uploadBuffer);
        DirectXDeviceMemory.CountReleased(
            memory: m_deviceContext.Memory,
            resource: m_texture
        );
        Release(pointer: ref m_texture);
    }

    /// <summary>Drains the queue, then releases the texture, upload buffer and command resources. A removed
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
                calls: m_calls,
                fence: ((ID3D12Fence*)m_fence),
                fenceEvent: m_fenceEvent,
                fenceValue: ref m_fenceValue,
                queue: ((ID3D12CommandQueue*)m_deviceContext.CommandQueueHandle)
            );
        }

        DisposeImageResources();
        Release(pointer: ref m_fence);
        Release(pointer: ref m_commandList);
        Release(pointer: ref m_commandAllocator);

        if (!m_fenceEvent.IsNull) {
            _ = PInvoke.CloseHandle(hObject: m_fenceEvent);
            m_fenceEvent = HANDLE.Null;
        }
    }
}
