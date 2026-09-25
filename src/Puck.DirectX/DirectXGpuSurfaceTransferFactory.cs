using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.DirectX.Apis;
using Puck.DirectX.Interfaces;
using Puck.DirectX.Interop;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.System.Com;
using static Puck.DirectX.DirectXConstants;

namespace Puck.DirectX;

/// <summary>
/// Implements <see cref="IGpuSurfaceTransferFactory"/> for Direct3D 12 by creating adapter wrappers over
/// <see cref="DirectXSurfaceUpload"/> and the inline readback and import helpers, each bound to the factory's device
/// context. Each wrapper converts <see cref="GpuPixelFormat"/> constants to <c>DXGI_FORMAT</c> values.
/// </summary>
/// <param name="deviceContext">The device context every object the factory creates works on.</param>
[SupportedOSPlatform("windows10.0.10240")]
public sealed class DirectXGpuSurfaceTransferFactory(DirectXDeviceContext deviceContext) : IGpuSurfaceTransferFactory {
    /// <inheritdoc/>
    public IGpuSurfaceReadback CreateReadback() =>
        new DirectXGpuSurfaceReadback(deviceContext: deviceContext);
    /// <inheritdoc/>
    public IGpuSurfaceUpload CreateUpload() =>
        new DirectXGpuSurfaceUpload(upload: new DirectXSurfaceUpload(deviceContext: deviceContext));
    /// <inheritdoc/>
    public IGpuSurfaceImport CreateImport() =>
        new DirectXGpuSurfaceImport(deviceContext: deviceContext);
}

[SupportedOSPlatform("windows10.0.10240")]
file sealed unsafe class DirectXGpuSurfaceReadback(IDirectXDeviceContext deviceContext) : IGpuSurfaceReadback {
    private nint m_readbackBuffer;
    private ulong m_readbackSize;
    private uint m_paddedRowPitch;
    private uint m_currentWidth;
    private uint m_currentHeight;
    private uint m_currentBytesPerPixel;
    private byte[]? m_outputBuffer;
    private bool m_disposed;

    public ReadOnlyMemory<byte> Read(
        nint sourceImageHandle,
        GpuPixelFormat format,
        uint width,
        uint height,
        uint bytesPerPixel,
        GpuImageLayout sourceLayout
    ) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        var device = ((ID3D12Device*)deviceContext.Device.Handle);

        EnsureReadbackBuffer(
            bytesPerPixel: bytesPerPixel,
            device: device,
            height: height,
            width: width
        );

        nint commandAllocator = 0;
        nint commandList = 0;

        // The allocator and list live for this read alone; a failed close or a lost device on the wait still
        // releases them.
        try {
            RecordCopyCommandList(
                commandAllocator: out commandAllocator,
                commandList: out commandList,
                device: device,
                format: format,
                height: height,
                sourceImageHandle: sourceImageHandle,
                sourceLayout: sourceLayout,
                width: width
            );

            var executable = ((ID3D12CommandList*)commandList);

            ((ID3D12CommandQueue*)deviceContext.CommandQueueHandle)->ExecuteCommandLists(
                NumCommandLists: 1,
                ppCommandLists: &executable
            );
            ((IGpuDeviceContext)deviceContext).WaitIdle();
        } finally {
            if (0 != commandList) {
                _ = ((IUnknown*)commandList)->Release();
            }

            if (0 != commandAllocator) {
                _ = ((IUnknown*)commandAllocator)->Release();
            }
        }

        return MapAndUnpackRows();
    }
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        ReleaseBuffer();
    }

    // (Re)creates the host-visible readback buffer + its row metadata when the extent/format first appears or changes;
    // a no-op when the current buffer already matches.
    private void EnsureReadbackBuffer(ID3D12Device* device, uint width, uint height, uint bytesPerPixel) {
        if (
            (0 != m_readbackBuffer) &&
            (m_currentWidth == width) &&
            (m_currentHeight == height) &&
            (m_currentBytesPerPixel == bytesPerPixel)
        ) {
            return;
        }

        ReleaseBuffer();

        var packedRowBytes = (width * bytesPerPixel);
        var paddedRowPitch = AlignRowPitch(packedRowBytes: packedRowBytes);
        var readbackByteLength = (((ulong)paddedRowPitch) * height);

        m_readbackBuffer = ((nint)DirectXBuffers.CreateCommitted(
            device: device,
            heapType: D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_READBACK,
            initialState: D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST,
            sizeBytes: readbackByteLength
        ));
        m_readbackSize = readbackByteLength;
        m_paddedRowPitch = paddedRowPitch;
        m_currentWidth = width;
        m_currentHeight = height;
        m_currentBytesPerPixel = bytesPerPixel;
        m_outputBuffer = new byte[(packedRowBytes * height)];
    }
    // The caller-supplied state is restored after the copy. ShaderReadOnly retains the original transition pair
    // byte-for-byte; General names storage images whose D3D12 resource state is UNORDERED_ACCESS.
    private void RecordCopyCommandList(ID3D12Device* device, nint sourceImageHandle, GpuPixelFormat format, uint width, uint height, GpuImageLayout sourceLayout, out nint commandAllocator, out nint commandList) {
        var dxgiFormat = DirectXGpuFormats.ToDxgiFormat(gpuPixelFormat: format);
        var sourceState = (DirectXGpuFormats.TryToResourceState(
            layout: sourceLayout,
            resourceState: out var resourceState
        )
            ? resourceState
            : throw new ArgumentOutOfRangeException(
                paramName: nameof(sourceLayout),
                actualValue: sourceLayout,
                message: "Readback requires an External, General, or ShaderReadOnly source image."
            )
        );

        device->CreateCommandAllocator(
            ppCommandAllocator: out var ca,
            riid: ID3D12CommandAllocator.IID_Guid,
            type: D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT
        );
        commandAllocator = ((nint)ca);

        device->CreateCommandList(
            nodeMask: 0,
            pCommandAllocator: ((ID3D12CommandAllocator*)commandAllocator),
            pInitialState: null,
            ppCommandList: out var cl,
            riid: ID3D12GraphicsCommandList.IID_Guid,
            type: D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT
        );
        commandList = ((nint)cl);

        var cmdList = ((ID3D12GraphicsCommandList*)commandList);
        var sourceResource = ((ID3D12Resource*)sourceImageHandle);

        var toCopySource = DirectXBarriers.Transition(
            after: D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_SOURCE,
            before: sourceState,
            resource: sourceResource
        );

        cmdList->ResourceBarrier(
            NumBarriers: 1,
            pBarriers: &toCopySource
        );

        var destLocation = new D3D12_TEXTURE_COPY_LOCATION {
            Type = D3D12_TEXTURE_COPY_TYPE.D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT,
            pResource = ((ID3D12Resource*)m_readbackBuffer),
        };

        destLocation.Anonymous.PlacedFootprint = new D3D12_PLACED_SUBRESOURCE_FOOTPRINT {
            Footprint = new D3D12_SUBRESOURCE_FOOTPRINT {
                Depth = 1,
                Format = dxgiFormat,
                Height = height,
                RowPitch = m_paddedRowPitch,
                Width = width,
            },
        };

        var srcLocation = new D3D12_TEXTURE_COPY_LOCATION {
            Type = D3D12_TEXTURE_COPY_TYPE.D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX,
            pResource = sourceResource,
        };

        srcLocation.Anonymous.SubresourceIndex = 0;

        cmdList->CopyTextureRegion(
            DstX: 0,
            DstY: 0,
            DstZ: 0,
            pDst: in destLocation,
            pSrc: in srcLocation,
            pSrcBox: ((D3D12_BOX?)null)
        );

        var toShaderResource = DirectXBarriers.Transition(
            after: sourceState,
            before: D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_SOURCE,
            resource: sourceResource
        );

        cmdList->ResourceBarrier(
            NumBarriers: 1,
            pBarriers: &toShaderResource
        );
        DirectXCommandCalls.Close(
            calls: DirectXDeviceCommandCalls.Of(deviceContext: deviceContext),
            commandList: cmdList
        );
    }
    // Maps the readback buffer, un-pads each row into the tightly packed output buffer, and unmaps.
    private ReadOnlyMemory<byte> MapAndUnpackRows() {
        var height = m_currentHeight;
        var packedRowBytes = (m_currentWidth * m_currentBytesPerPixel);

        var mapped = DirectXCommandCalls.Map(
            calls: DirectXDeviceCommandCalls.Of(deviceContext: deviceContext),
            resource: ((ID3D12Resource*)m_readbackBuffer)
        );

        try {
            var source = new ReadOnlySpan<byte>(
                length: checked((int)m_readbackSize),
                pointer: mapped
            );
            var output = m_outputBuffer!.AsSpan();

            for (var row = 0u; (row < height); row++) {
                source
                    .Slice(
                    length: ((int)packedRowBytes),
                    start: ((int)(row * m_paddedRowPitch))
                )
                    .CopyTo(destination: output.Slice(
                    length: ((int)packedRowBytes),
                    start: ((int)(row * packedRowBytes))
                ));
            }
        } finally {
            var writtenRange = new D3D12_RANGE { Begin = 0, End = 0, };

            ((ID3D12Resource*)m_readbackBuffer)->Unmap(
                Subresource: 0,
                pWrittenRange: &writtenRange
            );
        }

        return m_outputBuffer!;
    }
    private void ReleaseBuffer() {
        if (0 != m_readbackBuffer) {
            _ = ((IUnknown*)m_readbackBuffer)->Release();
            m_readbackBuffer = 0;
        }
    }
}
[SupportedOSPlatform("windows10.0.10240")]
file sealed class DirectXGpuSurfaceUpload(DirectXSurfaceUpload upload) : IGpuSurfaceUpload {
    private GCHandle m_currentToken;

    public nint Upload(
        ReadOnlyMemory<byte> pixels,
        GpuPixelFormat format,
        uint width,
        uint height
    ) {
        upload.Upload(
            pixels: pixels.Span,
            width: width,
            height: height,
            format: format
        );

        if (m_currentToken.IsAllocated) {
            m_currentToken.Free();
        }

        var imageView = new DirectXImageView {
            Format = DirectXGpuFormats.ToDxgiFormat(gpuPixelFormat: format),
            ResourceHandle = upload.TextureHandle,
        };

        m_currentToken = GCHandle.Alloc(value: imageView);

        return GCHandle.ToIntPtr(value: m_currentToken);
    }
    public void Dispose() {
        if (m_currentToken.IsAllocated) {
            m_currentToken.Free();
        }

        upload.Dispose();
    }
}
[SupportedOSPlatform("windows10.0.10240")]
file sealed unsafe class DirectXGpuSurfaceImport(IDirectXDeviceContext deviceContext) : IGpuSurfaceImport {
    // Cache the opened resource + view token by shared handle. A producer hands over the SAME handle every frame
    // (the exportable texture is stable), so without this each call would OpenSharedHandle again and leak an
    // ID3D12Resource per frame. Mirrors VulkanSurfaceImport's idempotent caching.
    private readonly Dictionary<nint, (nint Resource, GCHandle Token)> m_imports = [];

    private bool m_disposed;

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

        if (m_imports.TryGetValue(
            key: sharedHandle,
            value: out var cached
        )) {
            return new GpuImportedSurface(
                ImageHandle: cached.Resource,
                ImageViewHandle: GCHandle.ToIntPtr(value: cached.Token)
            );
        }

        var device = ((ID3D12Device*)deviceContext.Device.Handle);

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

        var token = GCHandle.Alloc(value: imageView);

        m_imports[sharedHandle] = (((nint)resource), token);

        return new GpuImportedSurface(
            ImageHandle: ((nint)resource),
            ImageViewHandle: GCHandle.ToIntPtr(value: token)
        );
    }
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;

        foreach (var (resource, token) in m_imports.Values) {
            if (0 != resource) {
                DirectXDeviceMemory.CountReleased(
                    memory: deviceContext.Memory,
                    resource: resource
                );
                _ = ((IUnknown*)resource)->Release();
            }

            if (token.IsAllocated) {
                token.Free();
            }
        }

        m_imports.Clear();
    }
}
