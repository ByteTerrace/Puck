using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.DirectX.Interfaces;
using Puck.DirectX.Interop;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.System.Com;
using static Puck.DirectX.DirectXConstants;

namespace Puck.DirectX;

/// <summary>
/// Implements <see cref="IGpuSurfaceTransferFactory"/> for Direct3D 12 by creating adapter wrappers over
/// <see cref="DirectXSurfaceUpload"/> and the inline readback and import helpers. Each wrapper downcasts
/// <see cref="IGpuDeviceContext"/> to <see cref="IDirectXDeviceContext"/> at call time and converts
/// <see cref="GpuPixelFormat"/> constants to <c>DXGI_FORMAT</c> / <see cref="DirectXPixelFormat"/> values.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed class DirectXGpuSurfaceTransferFactory : IGpuSurfaceTransferFactory {
    /// <inheritdoc/>
    public IGpuSurfaceReadback CreateReadback(IGpuDeviceContext deviceContext) =>
        new DirectXGpuSurfaceReadback(deviceContext: (IDirectXDeviceContext)deviceContext);
    /// <inheritdoc/>
    public IGpuSurfaceUpload CreateUpload(IGpuDeviceContext deviceContext) =>
        new DirectXGpuSurfaceUpload(upload: new DirectXSurfaceUpload(deviceContext: (IDirectXDeviceContext)deviceContext));
    /// <inheritdoc/>
    public IGpuSurfaceImport CreateImport(IGpuDeviceContext deviceContext) =>
        new DirectXGpuSurfaceImport(deviceContext: (IDirectXDeviceContext)deviceContext);
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
        IGpuDeviceContext deviceContextArg,
        nint sourceImageHandle,
        uint width,
        uint height,
        uint format,
        uint bytesPerPixel
    ) {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);

        var device = (ID3D12Device*)deviceContext.Device.Handle;
        var packedRowBytes = width * bytesPerPixel;
        var paddedRowPitch = ((packedRowBytes + TextureRowPitchAlignment) - 1) & ~(TextureRowPitchAlignment - 1);
        var readbackByteLength = (ulong)paddedRowPitch * height;

        if (
            (m_readbackBuffer == 0) ||
            (m_currentWidth != width) ||
            (m_currentHeight != height) ||
            (m_currentBytesPerPixel != bytesPerPixel)
        ) {
            ReleaseBuffer();

            var heapProperties = new D3D12_HEAP_PROPERTIES { Type = D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_READBACK };
            var bufferDesc = new D3D12_RESOURCE_DESC {
                DepthOrArraySize = 1,
                Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_BUFFER,
                Format = DXGI_FORMAT.DXGI_FORMAT_UNKNOWN,
                Height = 1,
                Layout = D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_ROW_MAJOR,
                MipLevels = 1,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, },
                Width = readbackByteLength,
            };

            void* buf;
            var resourceIid = ID3D12Resource.IID_Guid;

            device->CreateCommittedResource(
                in heapProperties,
                D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE,
                in bufferDesc,
                D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST,
                (D3D12_CLEAR_VALUE?)null,
                in resourceIid,
                &buf
            );

            m_readbackBuffer = (nint)buf;
            m_readbackSize = readbackByteLength;
            m_paddedRowPitch = paddedRowPitch;
            m_currentWidth = width;
            m_currentHeight = height;
            m_currentBytesPerPixel = bytesPerPixel;
            m_outputBuffer = new byte[packedRowBytes * height];
        }

        var dxgiFormat = DirectXGpuFormats.ToDxgiFormat(gpuPixelFormat: format);

        nint commandAllocator;
        nint commandList;

        device->CreateCommandAllocator(
            type: D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT,
            riid: ID3D12CommandAllocator.IID_Guid,
            ppCommandAllocator: out var ca
        );
        commandAllocator = (nint)ca;

        device->CreateCommandList(
            nodeMask: 0,
            type: D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT,
            pCommandAllocator: (ID3D12CommandAllocator*)commandAllocator,
            pInitialState: null,
            riid: ID3D12GraphicsCommandList.IID_Guid,
            ppCommandList: out var cl
        );
        commandList = (nint)cl;

        var cmdList = (ID3D12GraphicsCommandList*)commandList;
        var sourceResource = (ID3D12Resource*)sourceImageHandle;

        var toCopySource = new D3D12_RESOURCE_BARRIER {
            Type = D3D12_RESOURCE_BARRIER_TYPE.D3D12_RESOURCE_BARRIER_TYPE_TRANSITION,
        };

        toCopySource.Anonymous.Transition = new D3D12_RESOURCE_TRANSITION_BARRIER {
            pResource = sourceResource,
            Subresource = 0xFFFFFFFF,
            StateBefore = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE,
            StateAfter = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_SOURCE,
        };

        cmdList->ResourceBarrier(1, &toCopySource);

        var destLocation = new D3D12_TEXTURE_COPY_LOCATION {
            Type = D3D12_TEXTURE_COPY_TYPE.D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT,
            pResource = (ID3D12Resource*)m_readbackBuffer,
        };

        destLocation.Anonymous.PlacedFootprint = new D3D12_PLACED_SUBRESOURCE_FOOTPRINT {
            Footprint = new D3D12_SUBRESOURCE_FOOTPRINT {
                Depth = 1,
                Format = dxgiFormat,
                Height = height,
                RowPitch = paddedRowPitch,
                Width = width,
            },
        };

        var srcLocation = new D3D12_TEXTURE_COPY_LOCATION {
            Type = D3D12_TEXTURE_COPY_TYPE.D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX,
            pResource = sourceResource,
        };

        srcLocation.Anonymous.SubresourceIndex = 0;

        cmdList->CopyTextureRegion(in destLocation, 0, 0, 0, in srcLocation, (D3D12_BOX?)null);

        var toShaderResource = new D3D12_RESOURCE_BARRIER {
            Type = D3D12_RESOURCE_BARRIER_TYPE.D3D12_RESOURCE_BARRIER_TYPE_TRANSITION,
        };

        toShaderResource.Anonymous.Transition = new D3D12_RESOURCE_TRANSITION_BARRIER {
            pResource = sourceResource,
            Subresource = 0xFFFFFFFF,
            StateBefore = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_SOURCE,
            StateAfter = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE,
        };

        cmdList->ResourceBarrier(1, &toShaderResource);
        cmdList->Close();

        var executable = (ID3D12CommandList*)commandList;

        ((ID3D12CommandQueue*)deviceContext.CommandQueueHandle)->ExecuteCommandLists(1, &executable);
        ((IGpuDeviceContext)deviceContext).WaitIdle();

        _ = ((IUnknown*)commandList)->Release();
        _ = ((IUnknown*)commandAllocator)->Release();

        void* mapped;

        ((ID3D12Resource*)m_readbackBuffer)->Map(0, (D3D12_RANGE*)null, &mapped);

        try {
            var source = new ReadOnlySpan<byte>(pointer: mapped, length: checked((int)m_readbackSize));
            var output = m_outputBuffer!.AsSpan();

            for (var row = 0; (row < height); row++) {
                source
                    .Slice(start: (int)(row * m_paddedRowPitch), length: (int)packedRowBytes)
                    .CopyTo(destination: output.Slice(start: (int)(row * packedRowBytes), length: (int)packedRowBytes));
            }
        } finally {
            var writtenRange = new D3D12_RANGE { Begin = 0, End = 0, };

            ((ID3D12Resource*)m_readbackBuffer)->Unmap(0, &writtenRange);
        }

        return m_outputBuffer;
    }
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        ReleaseBuffer();
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
        IGpuDeviceContext deviceContext,
        ReadOnlyMemory<byte> pixels,
        uint width,
        uint height,
        uint format
    ) {
        var dxFormat = DirectXGpuFormats.ToDirectXPixelFormat(gpuPixelFormat: format);

        upload.Upload(
            pixels: pixels.Span,
            width: width,
            height: height,
            format: dxFormat
        );

        if (m_currentToken.IsAllocated) {
            m_currentToken.Free();
        }

        var imageView = new DirectXImageView {
            Format = DirectXGpuFormats.ToDxgiFormat(gpuPixelFormat: format),
            ResourceHandle = upload.TextureHandle,
        };

        m_currentToken = GCHandle.Alloc(imageView);

        return GCHandle.ToIntPtr(m_currentToken);
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

    public nint Import(
        IGpuDeviceContext deviceContextArg,
        nint sharedHandle,
        uint width,
        uint height,
        uint format
    ) {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);

        if (m_imports.TryGetValue(key: sharedHandle, value: out var cached)) {
            return GCHandle.ToIntPtr(cached.Token);
        }

        var device = (ID3D12Device*)deviceContext.Device.Handle;

        void* resource;
        var resourceIid = ID3D12Resource.IID_Guid;

        device->OpenSharedHandle(
            NTHandle: new Windows.Win32.Foundation.HANDLE((void*)sharedHandle),
            riid: &resourceIid,
            ppvObj: &resource
        );

        var imageView = new DirectXImageView {
            Format = DirectXGpuFormats.ToDxgiFormat(gpuPixelFormat: format),
            ResourceHandle = (nint)resource,
        };

        var token = GCHandle.Alloc(imageView);

        m_imports[sharedHandle] = ((nint)resource, token);

        return GCHandle.ToIntPtr(token);
    }
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;

        foreach (var (resource, token) in m_imports.Values) {
            if (0 != resource) {
                _ = ((IUnknown*)resource)->Release();
            }

            if (token.IsAllocated) {
                token.Free();
            }
        }

        m_imports.Clear();
    }
}
