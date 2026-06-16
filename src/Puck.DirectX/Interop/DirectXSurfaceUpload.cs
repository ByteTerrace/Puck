using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.DirectX.Interfaces;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.Security;

namespace Puck.DirectX.Interop;

/// <summary>
/// Materializes tightly packed <c>R8G8B8A8</c> CPU pixels into a shader-resource-view-sampled Direct3D 12
/// texture, against a shared <see cref="IDirectXDeviceContext"/>. It owns the default-heap texture, an
/// upload-heap staging buffer, a shader-visible SRV descriptor heap, and the command resources to drive the
/// copy, rebuilding them when the extent changes. Each <see cref="Upload"/> copies the pixels into the texture
/// and leaves it in the pixel-shader-resource state, then exposes the descriptor heap and GPU handle a textured
/// pipeline binds. This is the Direct3D 12 peer of <c>VulkanSurfaceUpload</c> — the consumer/ingest half that
/// lets a DirectX host sample a surface that arrived as host memory. Single-thread affine.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXSurfaceUpload : IDisposable {
    private const uint BytesPerPixel = 4;
    private const int GetCpuDescriptorHandleSlot = 9;
    private const int GetGpuDescriptorHandleSlot = 10;
    private const uint TextureRowPitchAlignment = 256;

    private readonly IDirectXDeviceContext m_deviceContext;
    private nint m_commandAllocator;
    private nint m_commandList;
    private bool m_disposed;
    private nint m_fence;
    private HANDLE m_fenceEvent;
    private ulong m_fenceValue;
    private DXGI_FORMAT m_format;
    private ulong m_gpuDescriptorPointer;
    private uint m_height;
    private uint m_paddedRowPitch;
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

        var device = (ID3D12Device*)deviceContext.Device.Handle;

        device->CreateCommandAllocator(
            type: D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT,
            riid: ID3D12CommandAllocator.IID_Guid,
            ppCommandAllocator: out var commandAllocator
        );
        m_commandAllocator = (nint)commandAllocator;

        device->CreateCommandList(
            nodeMask: 0,
            type: D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT,
            pCommandAllocator: (ID3D12CommandAllocator*)commandAllocator,
            pInitialState: null,
            riid: ID3D12GraphicsCommandList.IID_Guid,
            ppCommandList: out var commandList
        );
        m_commandList = (nint)commandList;
        ((ID3D12GraphicsCommandList*)commandList)->Close();

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

    /// <summary>Gets the native <c>ID3D12DescriptorHeap</c> handle to bind via <c>SetDescriptorHeaps</c>.</summary>
    public nint DescriptorHeapHandle => m_srvHeap;
    /// <summary>Gets the GPU descriptor handle (<c>D3D12_GPU_DESCRIPTOR_HANDLE.ptr</c>) of the texture's SRV.</summary>
    public ulong GpuDescriptorPointer => m_gpuDescriptorPointer;

    /// <summary>Copies tightly packed pixels into the SRV texture and leaves it sampleable.</summary>
    /// <param name="pixels">The tightly packed source pixels; at least <paramref name="width"/> × <paramref name="height"/> × 4 bytes.</param>
    /// <param name="width">The image width in pixels.</param>
    /// <param name="height">The image height in pixels.</param>
    /// <param name="format">The byte layout of the pixels, so the texture samples with correct channels.</param>
    /// <exception cref="ObjectDisposedException">The instance has been disposed.</exception>
    /// <exception cref="ArgumentException"><paramref name="pixels"/> is too small, or a dimension is zero.</exception>
    /// <exception cref="DirectXException">A Direct3D 12 call failed.</exception>
    public void Upload(ReadOnlySpan<byte> pixels, uint width, uint height, DirectXPixelFormat format) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        if (
            (0 == width) ||
            (0 == height)
        ) {
            throw new ArgumentException(message: "Texture dimensions must be non-zero.");
        }

        var packedRowBytes = checked((int)(width * BytesPerPixel));

        if (pixels.Length < (packedRowBytes * height)) {
            throw new ArgumentException(
                message: "The pixel span is smaller than the texture.",
                paramName: nameof(pixels)
            );
        }

        EnsureResources(
            format: ToDxgiFormat(format: format),
            height: height,
            width: width
        );
        WriteUploadBuffer(
            packedRowBytes: packedRowBytes,
            pixels: pixels
        );

        var commandList = (ID3D12GraphicsCommandList*)m_commandList;
        var allocator = (ID3D12CommandAllocator*)m_commandAllocator;
        var texture = (ID3D12Resource*)m_texture;

        allocator->Reset();
        commandList->Reset(
            pAllocator: allocator,
            pInitialState: null
        );

        if (D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST != m_textureState) {
            var toCopyDestination = CreateTransition(
                after: D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST,
                before: m_textureState,
                resource: texture
            );

            commandList->ResourceBarrier(
                1,
                &toCopyDestination
            );
        }

        var destinationLocation = new D3D12_TEXTURE_COPY_LOCATION {
            Type = D3D12_TEXTURE_COPY_TYPE.D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX,
            pResource = texture,
        };

        destinationLocation.Anonymous.SubresourceIndex = 0;

        var sourceLocation = new D3D12_TEXTURE_COPY_LOCATION {
            Type = D3D12_TEXTURE_COPY_TYPE.D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT,
            pResource = (ID3D12Resource*)m_uploadBuffer,
        };

        sourceLocation.Anonymous.PlacedFootprint = new D3D12_PLACED_SUBRESOURCE_FOOTPRINT {
            Footprint = new D3D12_SUBRESOURCE_FOOTPRINT {
                Depth = 1,
                Format = m_format,
                Height = m_height,
                RowPitch = m_paddedRowPitch,
                Width = m_width,
            },
            Offset = 0,
        };

        commandList->CopyTextureRegion(
            in destinationLocation,
            0,
            0,
            0,
            in sourceLocation,
            (D3D12_BOX?)null
        );

        var toShaderResource = CreateTransition(
            after: D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE,
            before: D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST,
            resource: texture
        );

        commandList->ResourceBarrier(
            1,
            &toShaderResource
        );
        commandList->Close();
        m_textureState = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE;

        var executable = (ID3D12CommandList*)commandList;

        ((ID3D12CommandQueue*)m_deviceContext.CommandQueueHandle)->ExecuteCommandLists(
            1,
            &executable
        );
        WaitForGpu();
    }

    private static D3D12_RESOURCE_BARRIER CreateTransition(ID3D12Resource* resource, D3D12_RESOURCE_STATES before, D3D12_RESOURCE_STATES after) {
        var barrier = new D3D12_RESOURCE_BARRIER {
            Type = D3D12_RESOURCE_BARRIER_TYPE.D3D12_RESOURCE_BARRIER_TYPE_TRANSITION,
        };

        barrier.Anonymous.Transition = new D3D12_RESOURCE_TRANSITION_BARRIER {
            StateAfter = after,
            StateBefore = before,
            Subresource = 0xFFFFFFFF,
            pResource = resource,
        };

        return barrier;
    }
    private static D3D12_CPU_DESCRIPTOR_HANDLE GetCpuHeapStart(ID3D12DescriptorHeap* heap) {
        D3D12_CPU_DESCRIPTOR_HANDLE handle;
        var vtable = *(void***)heap;

        ((delegate* unmanaged[Stdcall]<ID3D12DescriptorHeap*, D3D12_CPU_DESCRIPTOR_HANDLE*, void>)vtable[GetCpuDescriptorHandleSlot])(
            heap,
            &handle
        );

        return handle;
    }
    private static D3D12_GPU_DESCRIPTOR_HANDLE GetGpuHeapStart(ID3D12DescriptorHeap* heap) {
        D3D12_GPU_DESCRIPTOR_HANDLE handle;
        var vtable = *(void***)heap;

        // Same return-by-hidden-pointer ABI workaround as the CPU handle (see DirectXSwapChainRenderer).
        ((delegate* unmanaged[Stdcall]<ID3D12DescriptorHeap*, D3D12_GPU_DESCRIPTOR_HANDLE*, void>)vtable[GetGpuDescriptorHandleSlot])(
            heap,
            &handle
        );

        return handle;
    }
    private static void Release(ref nint pointer) {
        if (0 != pointer) {
            _ = ((Windows.Win32.System.Com.IUnknown*)pointer)->Release();
            pointer = 0;
        }
    }
    private static DXGI_FORMAT ToDxgiFormat(DirectXPixelFormat format) {
        return format switch {
            DirectXPixelFormat.B8G8R8A8Unorm => DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            DirectXPixelFormat.R8G8B8A8Unorm => DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM,
            _ => throw new ArgumentOutOfRangeException(
                actualValue: format,
                message: "The pixel format has no DXGI mapping.",
                paramName: nameof(format)
            ),
        };
    }
    private void EnsureResources(uint width, uint height, DXGI_FORMAT format) {
        if (
            (0 != m_texture) &&
            (m_width == width) &&
            (m_height == height) &&
            (m_format == format)
        ) {
            return;
        }

        DisposeImageResources();

        var device = (ID3D12Device*)m_deviceContext.Device.Handle;
        var textureHeapProperties = new D3D12_HEAP_PROPERTIES {
            Type = D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT,
        };
        var textureDesc = new D3D12_RESOURCE_DESC {
            DepthOrArraySize = 1,
            Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_TEXTURE2D,
            Format = format,
            Height = height,
            Layout = D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_UNKNOWN,
            MipLevels = 1,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, },
            Width = width,
        };

        void* texture;
        var resourceIid = ID3D12Resource.IID_Guid;

        device->CreateCommittedResource(
            in textureHeapProperties,
            D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE,
            in textureDesc,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST,
            (D3D12_CLEAR_VALUE?)null,
            in resourceIid,
            &texture
        );
        m_texture = (nint)texture;
        m_textureState = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST;

        m_paddedRowPitch = (((width * BytesPerPixel) + TextureRowPitchAlignment) - 1) & ~(TextureRowPitchAlignment - 1);

        var uploadHeapProperties = new D3D12_HEAP_PROPERTIES {
            Type = D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_UPLOAD,
        };
        var uploadDesc = new D3D12_RESOURCE_DESC {
            DepthOrArraySize = 1,
            Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_BUFFER,
            Format = DXGI_FORMAT.DXGI_FORMAT_UNKNOWN,
            Height = 1,
            Layout = D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_ROW_MAJOR,
            MipLevels = 1,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, },
            Width = ((ulong)m_paddedRowPitch * height),
        };

        void* uploadBuffer;

        device->CreateCommittedResource(
            in uploadHeapProperties,
            D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE,
            in uploadDesc,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_GENERIC_READ,
            (D3D12_CLEAR_VALUE?)null,
            in resourceIid,
            &uploadBuffer
        );
        m_uploadBuffer = (nint)uploadBuffer;

        if (0 == m_srvHeap) {
            var heapDesc = new D3D12_DESCRIPTOR_HEAP_DESC {
                Flags = D3D12_DESCRIPTOR_HEAP_FLAGS.D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE,
                NumDescriptors = 1,
                Type = D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV,
            };

            device->CreateDescriptorHeap(
                pDescriptorHeapDesc: in heapDesc,
                riid: ID3D12DescriptorHeap.IID_Guid,
                ppvHeap: out var srvHeap
            );
            m_srvHeap = (nint)srvHeap;
            m_gpuDescriptorPointer = GetGpuHeapStart(heap: (ID3D12DescriptorHeap*)srvHeap).ptr;
        }

        var srvDesc = new D3D12_SHADER_RESOURCE_VIEW_DESC {
            Format = format,
            // D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING — identity RGBA swizzle.
            Shader4ComponentMapping = 5768,
            ViewDimension = D3D12_SRV_DIMENSION.D3D12_SRV_DIMENSION_TEXTURE2D,
        };

        srvDesc.Anonymous.Texture2D = new D3D12_TEX2D_SRV {
            MipLevels = 1,
            MostDetailedMip = 0,
            PlaneSlice = 0,
            ResourceMinLODClamp = 0f,
        };

        device->CreateShaderResourceView(
            pResource: (ID3D12Resource*)m_texture,
            pDesc: &srvDesc,
            DestDescriptor: GetCpuHeapStart(heap: (ID3D12DescriptorHeap*)m_srvHeap)
        );

        m_format = format;
        m_height = height;
        m_width = width;
    }
    private void WriteUploadBuffer(ReadOnlySpan<byte> pixels, int packedRowBytes) {
        var uploadBuffer = (ID3D12Resource*)m_uploadBuffer;

        void* mapped;

        uploadBuffer->Map(
            0,
            (D3D12_RANGE*)null,
            &mapped
        );

        try {
            var destination = new Span<byte>(
                pointer: mapped,
                length: checked((int)((ulong)m_paddedRowPitch * m_height))
            );
            var paddedRowPitch = checked((int)m_paddedRowPitch);

            for (var row = 0; (row < m_height); row++) {
                pixels
                    .Slice(
                        start: (row * packedRowBytes),
                        length: packedRowBytes
                    )
                    .CopyTo(destination: destination.Slice(
                        start: (row * paddedRowPitch),
                        length: packedRowBytes
                    ));
            }
        } finally {
            uploadBuffer->Unmap(
                0,
                (D3D12_RANGE*)null
            );
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
    private void DisposeImageResources() {
        Release(pointer: ref m_uploadBuffer);
        Release(pointer: ref m_texture);
    }

    /// <summary>Waits for the GPU, then releases the texture, upload buffer, SRV heap, and command resources. Safe to call more than once.</summary>
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
