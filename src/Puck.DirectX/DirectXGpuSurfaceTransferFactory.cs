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
    private nint m_fence;
    private ulong m_fenceValue;
    private ulong m_pendingFenceValue;
    private nint m_deferredCommandAllocator;
    private nint m_deferredCommandList;
    private bool m_readInFlight;
    private bool m_disposed;

    public ReadOnlyMemory<byte> Read(
        IGpuDeviceContext deviceContextArg,
        nint sourceImageHandle,
        GpuPixelFormat format,
        uint width,
        uint height,
        uint bytesPerPixel
    ) {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);

        var device = (ID3D12Device*)deviceContext.Device.Handle;

        EnsureReadbackBuffer(bytesPerPixel: bytesPerPixel, device: device, height: height, width: width);
        RecordCopyCommandList(commandAllocator: out var commandAllocator, commandList: out var commandList, device: device, format: format, height: height, sourceImageHandle: sourceImageHandle, width: width);

        var executable = (ID3D12CommandList*)commandList;

        ((ID3D12CommandQueue*)deviceContext.CommandQueueHandle)->ExecuteCommandLists(1, &executable);
        ((IGpuDeviceContext)deviceContext).WaitIdle();

        _ = ((IUnknown*)commandList)->Release();
        _ = ((IUnknown*)commandAllocator)->Release();

        return MapAndUnpackRows();
    }
    public void SubmitRead(
        IGpuDeviceContext deviceContextArg,
        nint sourceImageHandle,
        GpuPixelFormat format,
        uint width,
        uint height,
        uint bytesPerPixel
    ) {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);

        if (m_readInFlight) {
            throw new InvalidOperationException(message: "A readback is already in flight; map it with MapPixels before submitting another.");
        }

        var device = (ID3D12Device*)deviceContext.Device.Handle;

        EnsureReadbackBuffer(bytesPerPixel: bytesPerPixel, device: device, height: height, width: width);
        EnsureFence(device: device);
        RecordCopyCommandList(commandAllocator: out var commandAllocator, commandList: out var commandList, device: device, format: format, height: height, sourceImageHandle: sourceImageHandle, width: width);

        var executable = (ID3D12CommandList*)commandList;
        var queue = (ID3D12CommandQueue*)deviceContext.CommandQueueHandle;

        queue->ExecuteCommandLists(1, &executable);

        // DEFER releasing the allocator/list until MapPixels — the GPU is still consuming them (there is NO WaitIdle
        // here); releasing them now would be a use-after-free. A completion Signal makes IsReadComplete pollable.
        m_deferredCommandAllocator = commandAllocator;
        m_deferredCommandList = commandList;
        m_pendingFenceValue = ++m_fenceValue;

        queue->Signal((ID3D12Fence*)m_fence, m_pendingFenceValue);

        m_readInFlight = true;
    }
    public bool IsReadComplete() {
        if (
            m_disposed ||
            (0 == m_fence) ||
            !m_readInFlight
        ) {
            return false;
        }

        // Non-blocking poll of the deferred copy's fence. A REMOVED device returns UINT64_MAX from GetCompletedValue —
        // never a real value (we only ever Signal small monotonic counts) — so treat it as NOT complete. That matches
        // the Vulkan backend and the IGpuSurfaceReadback contract (a torn-down device polls false), letting the caller
        // drain-and-drop the engine rather than mapping a dead resource. Never throws.
        var completed = ((ID3D12Fence*)m_fence)->GetCompletedValue();

        return ((completed != ulong.MaxValue) && (completed >= m_pendingFenceValue));
    }
    public ReadOnlyMemory<byte> MapPixels() {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);

        var pixels = MapAndUnpackRows();

        // The deferred copy has completed (the caller polled IsReadComplete), so the allocator/list may now be released.
        ReleaseDeferredCommands();
        m_readInFlight = false;

        return pixels;
    }
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;

        // A deferred (submitted, not-yet-mapped) copy may still be executing into the very resources ReleaseBuffer
        // frees — BakeRasterizer.DrainPending's give-up path disposes the engine with a read still in flight. Wait out
        // that copy's own fence first (mirrors the Vulkan readback's TryWaitIdle). GetCompletedValue never throws, and a
        // removed device returns UINT64_MAX (>= pending) so this exits immediately rather than hanging teardown.
        if (m_readInFlight && (0 != m_fence)) {
            var fence = (ID3D12Fence*)m_fence;

            while (fence->GetCompletedValue() < m_pendingFenceValue) {
                // Spin out the already-submitted copy; this waits only the GPU, never the sim clock.
            }
        }

        ReleaseBuffer();
    }

    // (Re)creates the host-visible readback buffer + its row metadata when the extent/format first appears or changes;
    // a no-op when the current buffer already matches. Shared by the blocking Read and the pipelined SubmitRead.
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

        var packedRowBytes = width * bytesPerPixel;
        var paddedRowPitch = ((packedRowBytes + TextureRowPitchAlignment) - 1) & ~(TextureRowPitchAlignment - 1);
        var readbackByteLength = (ulong)paddedRowPitch * height;
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

    // Creates a per-copy allocator + list and records barrier → CopyTextureRegion → barrier → Close: the source image
    // (left in its pixel-shader-resource state) is copied into the readback buffer's placed footprint.
    private void RecordCopyCommandList(ID3D12Device* device, nint sourceImageHandle, GpuPixelFormat format, uint width, uint height, out nint commandAllocator, out nint commandList) {
        var dxgiFormat = DirectXGpuFormats.ToDxgiFormat(gpuPixelFormat: format);

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
                RowPitch = m_paddedRowPitch,
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
    }

    // Maps the readback buffer, un-pads each row into the tightly packed output buffer, and unmaps. Shared by the
    // blocking Read (immediately after the wait) and the pipelined MapPixels (after the fence poll reports complete).
    private ReadOnlyMemory<byte> MapAndUnpackRows() {
        var height = m_currentHeight;
        var packedRowBytes = m_currentWidth * m_currentBytesPerPixel;

        void* mapped;

        ((ID3D12Resource*)m_readbackBuffer)->Map(0, (D3D12_RANGE*)null, &mapped);

        try {
            var source = new ReadOnlySpan<byte>(pointer: mapped, length: checked((int)m_readbackSize));
            var output = m_outputBuffer!.AsSpan();

            for (var row = 0u; (row < height); row++) {
                source
                    .Slice(start: (int)(row * m_paddedRowPitch), length: (int)packedRowBytes)
                    .CopyTo(destination: output.Slice(start: (int)(row * packedRowBytes), length: (int)packedRowBytes));
            }
        } finally {
            var writtenRange = new D3D12_RANGE { Begin = 0, End = 0, };

            ((ID3D12Resource*)m_readbackBuffer)->Unmap(0, &writtenRange);
        }

        return m_outputBuffer!;
    }

    // Lazily creates the completion fence for the pipelined SubmitRead path (mirrors DirectXDeviceContext's idle
    // fence, minus the event — the poll reads GetCompletedValue directly). A no-op once created; released in ReleaseBuffer.
    private void EnsureFence(ID3D12Device* device) {
        if (0 != m_fence) {
            return;
        }

        // CreateFence is PreserveSig=false in these bindings (returns void, throws on failure), so a failure surfaces
        // as an exception here — caught by BakePreviewService.Tick and degraded to the last image — rather than leaving
        // m_fence at 0 for a later null Signal.
        device->CreateFence(
            InitialValue: 0,
            Flags: default,
            riid: ID3D12Fence.IID_Guid,
            ppFence: out var fence
        );
        m_fence = (nint)fence;
        m_fenceValue = 0;
    }

    private void ReleaseBuffer() {
        if (0 != m_readbackBuffer) {
            _ = ((IUnknown*)m_readbackBuffer)->Release();
            m_readbackBuffer = 0;
        }

        ReleaseDeferredCommands();

        if (0 != m_fence) {
            _ = ((IUnknown*)m_fence)->Release();
            m_fence = 0;
            m_fenceValue = 0;
        }

        m_readInFlight = false;
    }
    private void ReleaseDeferredCommands() {
        if (0 != m_deferredCommandList) {
            _ = ((IUnknown*)m_deferredCommandList)->Release();
            m_deferredCommandList = 0;
        }

        if (0 != m_deferredCommandAllocator) {
            _ = ((IUnknown*)m_deferredCommandAllocator)->Release();
            m_deferredCommandAllocator = 0;
        }
    }
}
[SupportedOSPlatform("windows10.0.10240")]
file sealed class DirectXGpuSurfaceUpload(DirectXSurfaceUpload upload) : IGpuSurfaceUpload {
    private GCHandle m_currentToken;

    public nint Upload(
        IGpuDeviceContext deviceContext,
        ReadOnlyMemory<byte> pixels,
        GpuPixelFormat format,
        uint width,
        uint height
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
        GpuPixelFormat format,
        uint width,
        uint height
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
