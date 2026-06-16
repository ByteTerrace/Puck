using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.DirectX.Interfaces;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.Security;

namespace Puck.DirectX.Interop;

/// <summary>
/// A windowless Direct3D 12 producer: against a shared <see cref="IDirectXDeviceContext"/>'s device and queue,
/// it owns an offscreen render-target texture and a host-visible readback buffer, and exposes a single
/// <see cref="RenderInto"/> that clears the texture to a color, copies it to the readback buffer, and de-pads
/// the result into a tightly packed <c>R8G8B8A8_UNORM</c> CPU image. This is the cross-backend transport's
/// producer half — the rendered pixels come back to host memory so a consumer on another device (a Vulkan
/// host) can upload and sample them. It resolves its device from the host the same way a Vulkan node does,
/// rather than creating its own. Single-thread affine: create and drive it on one thread.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXOffscreenRenderer : IDisposable {
    private const uint AllSubresources = 0xFFFFFFFF;
    // The byte size of one R8G8B8A8 texel, and D3D12's required row-pitch alignment for buffer-image copies.
    private const uint BytesPerPixel = 4;
    private const uint TextureRowPitchAlignment = 256;
    // The vtable slot of ID3D12DescriptorHeap::GetCPUDescriptorHandleForHeapStart (see DirectXSwapChainRenderer).
    private const int GetCpuDescriptorHandleSlot = 9;

    private readonly IDirectXDeviceContext m_deviceContext;
    private readonly uint m_height;
    private readonly uint m_packedRowBytes;
    private readonly uint m_paddedRowPitch;
    private readonly ulong m_readbackByteLength;
    private readonly uint m_width;
    private nint m_commandAllocator;
    private nint m_commandList;
    private bool m_disposed;
    private nint m_fence;
    private HANDLE m_fenceEvent;
    private ulong m_fenceValue;
    private nint m_readbackBuffer;
    private nint m_renderTarget;
    private nint m_rtvHeap;

    /// <summary>Initializes a new instance of the <see cref="DirectXOffscreenRenderer"/> class.</summary>
    /// <param name="deviceContext">The shared device context whose device and command queue the producer renders on.</param>
    /// <param name="width">The offscreen image width in pixels.</param>
    /// <param name="height">The offscreen image height in pixels.</param>
    /// <exception cref="ArgumentNullException"><paramref name="deviceContext"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A dimension is zero.</exception>
    /// <exception cref="DirectXException">A Direct3D 12 call failed.</exception>
    public DirectXOffscreenRenderer(IDirectXDeviceContext deviceContext, uint width, uint height) {
        ArgumentNullException.ThrowIfNull(deviceContext);

        if (
            (0 == width) ||
            (0 == height)
        ) {
            throw new ArgumentException(message: "Offscreen dimensions must be non-zero.");
        }

        m_deviceContext = deviceContext;
        m_height = height;
        m_packedRowBytes = (width * BytesPerPixel);
        m_paddedRowPitch = ((m_packedRowBytes + TextureRowPitchAlignment) - 1) & ~(TextureRowPitchAlignment - 1);
        m_readbackByteLength = ((ulong)m_paddedRowPitch * height);
        m_width = width;

        Initialize();
    }

    /// <summary>Gets the offscreen image height in pixels.</summary>
    public uint Height => m_height;
    /// <summary>Gets the tightly packed byte length of one rendered image (<c>width * height * 4</c>).</summary>
    public int PixelByteLength => checked((int)(m_packedRowBytes * m_height));
    /// <summary>Gets the offscreen image width in pixels.</summary>
    public uint Width => m_width;

    private static D3D12_RESOURCE_BARRIER CreateTransition(ID3D12Resource* resource, D3D12_RESOURCE_STATES before, D3D12_RESOURCE_STATES after) {
        var barrier = new D3D12_RESOURCE_BARRIER {
            Type = D3D12_RESOURCE_BARRIER_TYPE.D3D12_RESOURCE_BARRIER_TYPE_TRANSITION,
        };

        barrier.Anonymous.Transition = new D3D12_RESOURCE_TRANSITION_BARRIER {
            StateAfter = after,
            StateBefore = before,
            Subresource = AllSubresources,
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
    private static void Release(ref nint pointer) {
        if (0 != pointer) {
            _ = ((Windows.Win32.System.Com.IUnknown*)pointer)->Release();
            pointer = 0;
        }
    }
    private void Initialize() {
        var device = (ID3D12Device*)m_deviceContext.Device.Handle;

        CreateRenderTarget();
        CreateReadbackBuffer();

        ((ID3D12Device*)device)->CreateCommandAllocator(
            type: D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT,
            riid: ID3D12CommandAllocator.IID_Guid,
            ppCommandAllocator: out var commandAllocator
        );
        m_commandAllocator = (nint)commandAllocator;

        ((ID3D12Device*)device)->CreateCommandList(
            nodeMask: 0,
            type: D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT,
            pCommandAllocator: (ID3D12CommandAllocator*)commandAllocator,
            pInitialState: null,
            riid: ID3D12GraphicsCommandList.IID_Guid,
            ppCommandList: out var commandList
        );
        m_commandList = (nint)commandList;
        ((ID3D12GraphicsCommandList*)commandList)->Close();

        ((ID3D12Device*)device)->CreateFence(
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
    private void CreateRenderTarget() {
        var device = (ID3D12Device*)m_deviceContext.Device.Handle;
        var heapProperties = new D3D12_HEAP_PROPERTIES {
            Type = D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT,
        };
        var textureDesc = new D3D12_RESOURCE_DESC {
            DepthOrArraySize = 1,
            Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_TEXTURE2D,
            Flags = D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET,
            Format = DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM,
            Height = m_height,
            Layout = D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_UNKNOWN,
            MipLevels = 1,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, },
            Width = m_width,
        };

        void* renderTarget;
        var resourceIid = ID3D12Resource.IID_Guid;

        device->CreateCommittedResource(
            in heapProperties,
            D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE,
            in textureDesc,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET,
            (D3D12_CLEAR_VALUE?)null,
            in resourceIid,
            &renderTarget
        );
        m_renderTarget = (nint)renderTarget;

        var heapDesc = new D3D12_DESCRIPTOR_HEAP_DESC {
            NumDescriptors = 1,
            Type = D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_RTV,
        };

        device->CreateDescriptorHeap(
            pDescriptorHeapDesc: in heapDesc,
            riid: ID3D12DescriptorHeap.IID_Guid,
            ppvHeap: out var rtvHeap
        );
        m_rtvHeap = (nint)rtvHeap;

        device->CreateRenderTargetView(
            pResource: (ID3D12Resource*)renderTarget,
            pDesc: null,
            DestDescriptor: GetCpuHeapStart(heap: (ID3D12DescriptorHeap*)rtvHeap)
        );
    }
    private void CreateReadbackBuffer() {
        var heapProperties = new D3D12_HEAP_PROPERTIES {
            Type = D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_READBACK,
        };
        var bufferDesc = new D3D12_RESOURCE_DESC {
            DepthOrArraySize = 1,
            Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_BUFFER,
            Format = DXGI_FORMAT.DXGI_FORMAT_UNKNOWN,
            Height = 1,
            Layout = D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_ROW_MAJOR,
            MipLevels = 1,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, },
            Width = m_readbackByteLength,
        };

        void* readbackBuffer;
        var resourceIid = ID3D12Resource.IID_Guid;

        ((ID3D12Device*)m_deviceContext.Device.Handle)->CreateCommittedResource(
            in heapProperties,
            D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE,
            in bufferDesc,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST,
            (D3D12_CLEAR_VALUE?)null,
            in resourceIid,
            &readbackBuffer
        );
        m_readbackBuffer = (nint)readbackBuffer;
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

    /// <summary>Clears the offscreen texture, draws the given geometry over it, reads the result back, and
    /// writes the tightly packed <c>R8G8B8A8_UNORM</c> pixels into <paramref name="destination"/>, blocking
    /// until the GPU is done.</summary>
    /// <param name="destination">The buffer to receive the pixels; must be at least <see cref="PixelByteLength"/> bytes.</param>
    /// <param name="pipeline">The graphics pipeline (root signature + pipeline state) to draw with.</param>
    /// <param name="texture">The texture to bind to the pipeline's SRV descriptor table, or <see langword="null"/> for a pipeline that samples nothing.</param>
    /// <param name="vertexBuffer">The vertex buffer to bind at slot 0.</param>
    /// <param name="vertexCount">The number of vertices to draw.</param>
    /// <param name="clearRed">The background red component, in the range [0, 1].</param>
    /// <param name="clearGreen">The background green component, in the range [0, 1].</param>
    /// <param name="clearBlue">The background blue component, in the range [0, 1].</param>
    /// <param name="clearAlpha">The background alpha component, in the range [0, 1].</param>
    /// <exception cref="ObjectDisposedException">The renderer has been disposed.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="pipeline"/> or <paramref name="vertexBuffer"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too small.</exception>
    /// <exception cref="DirectXException">A Direct3D 12 call failed.</exception>
    public void RenderInto(
        Span<byte> destination,
        DirectXPipeline pipeline,
        DirectXSurfaceUpload? texture,
        DirectXVertexBuffer vertexBuffer,
        uint vertexCount,
        float clearRed,
        float clearGreen,
        float clearBlue,
        float clearAlpha
    ) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(vertexBuffer);

        if (destination.Length < PixelByteLength) {
            throw new ArgumentException(
                message: $"The destination buffer ({destination.Length} bytes) is smaller than one rendered image ({PixelByteLength} bytes).",
                paramName: nameof(destination)
            );
        }

        var allocator = (ID3D12CommandAllocator*)m_commandAllocator;
        var commandList = (ID3D12GraphicsCommandList*)m_commandList;
        var renderTarget = (ID3D12Resource*)m_renderTarget;

        allocator->Reset();
        commandList->Reset(
            pAllocator: allocator,
            pInitialState: null
        );

        var renderTargetView = GetCpuHeapStart(heap: (ID3D12DescriptorHeap*)m_rtvHeap);

        commandList->OMSetRenderTargets(
            NumRenderTargetDescriptors: 1,
            pRenderTargetDescriptors: &renderTargetView,
            RTsSingleHandleToDescriptorRange: false,
            pDepthStencilDescriptor: null
        );

        var color = stackalloc float[4] { clearRed, clearGreen, clearBlue, clearAlpha, };

        commandList->ClearRenderTargetView(
            renderTargetView,
            color,
            0,
            null
        );

        var viewport = new D3D12_VIEWPORT {
            Height = m_height,
            MaxDepth = 1f,
            MinDepth = 0f,
            TopLeftX = 0f,
            TopLeftY = 0f,
            Width = m_width,
        };
        var scissorRect = new RECT {
            bottom = (int)m_height,
            left = 0,
            right = (int)m_width,
            top = 0,
        };
        var vertexBufferView = new D3D12_VERTEX_BUFFER_VIEW {
            BufferLocation = vertexBuffer.BufferLocation,
            SizeInBytes = vertexBuffer.SizeBytes,
            StrideInBytes = vertexBuffer.StrideBytes,
        };

        commandList->RSSetViewports(
            1,
            &viewport
        );
        commandList->RSSetScissorRects(
            1,
            &scissorRect
        );
        commandList->SetGraphicsRootSignature((ID3D12RootSignature*)pipeline.RootSignatureHandle);

        if (texture is not null) {
            var descriptorHeap = (ID3D12DescriptorHeap*)texture.DescriptorHeapHandle;

            commandList->SetDescriptorHeaps(
                1,
                &descriptorHeap
            );
            commandList->SetGraphicsRootDescriptorTable(
                0,
                new D3D12_GPU_DESCRIPTOR_HANDLE { ptr = texture.GpuDescriptorPointer, }
            );
        }

        commandList->SetPipelineState((ID3D12PipelineState*)pipeline.PipelineStateHandle);
        commandList->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        commandList->IASetVertexBuffers(
            0,
            1,
            &vertexBufferView
        );
        commandList->DrawInstanced(
            vertexCount,
            1,
            0,
            0
        );

        var toCopySource = CreateTransition(
            after: D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_SOURCE,
            before: D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET,
            resource: renderTarget
        );

        commandList->ResourceBarrier(
            1,
            &toCopySource
        );

        var destinationLocation = new D3D12_TEXTURE_COPY_LOCATION {
            Type = D3D12_TEXTURE_COPY_TYPE.D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT,
            pResource = (ID3D12Resource*)m_readbackBuffer,
        };

        destinationLocation.Anonymous.PlacedFootprint = new D3D12_PLACED_SUBRESOURCE_FOOTPRINT {
            Footprint = new D3D12_SUBRESOURCE_FOOTPRINT {
                Depth = 1,
                Format = DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM,
                Height = m_height,
                RowPitch = m_paddedRowPitch,
                Width = m_width,
            },
            Offset = 0,
        };

        var sourceLocation = new D3D12_TEXTURE_COPY_LOCATION {
            Type = D3D12_TEXTURE_COPY_TYPE.D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX,
            pResource = renderTarget,
        };

        sourceLocation.Anonymous.SubresourceIndex = 0;

        commandList->CopyTextureRegion(
            in destinationLocation,
            0,
            0,
            0,
            in sourceLocation,
            (D3D12_BOX?)null
        );

        var toRenderTarget = CreateTransition(
            after: D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET,
            before: D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_SOURCE,
            resource: renderTarget
        );

        commandList->ResourceBarrier(
            1,
            &toRenderTarget
        );
        commandList->Close();

        var executable = (ID3D12CommandList*)commandList;

        ((ID3D12CommandQueue*)m_deviceContext.CommandQueueHandle)->ExecuteCommandLists(
            1,
            &executable
        );
        WaitForGpu();

        CopyReadbackInto(destination: destination);
    }

    private void CopyReadbackInto(Span<byte> destination) {
        var readbackBuffer = (ID3D12Resource*)m_readbackBuffer;

        void* mapped;

        readbackBuffer->Map(
            0,
            (D3D12_RANGE*)null,
            &mapped
        );

        try {
            var source = new ReadOnlySpan<byte>(
                pointer: mapped,
                length: checked((int)m_readbackByteLength)
            );
            var packedRowBytes = checked((int)m_packedRowBytes);
            var paddedRowPitch = checked((int)m_paddedRowPitch);

            for (var row = 0; (row < m_height); row++) {
                source
                    .Slice(
                        start: (row * paddedRowPitch),
                        length: packedRowBytes
                    )
                    .CopyTo(destination: destination.Slice(
                        start: (row * packedRowBytes),
                        length: packedRowBytes
                    ));
            }
        } finally {
            // An empty written range tells the runtime the CPU wrote nothing back.
            var writtenRange = new D3D12_RANGE { Begin = 0, End = 0, };

            readbackBuffer->Unmap(
                0,
                &writtenRange
            );
        }
    }

    /// <summary>Waits for the GPU to finish, then releases every owned Direct3D 12 object. Safe to call more than once.</summary>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;

        // The device and command queue belong to the shared device context, not to this producer.
        if (
            (0 != m_deviceContext.CommandQueueHandle) &&
            (0 != m_fence)
        ) {
            WaitForGpu();
        }

        Release(pointer: ref m_readbackBuffer);
        Release(pointer: ref m_renderTarget);
        Release(pointer: ref m_rtvHeap);
        Release(pointer: ref m_fence);
        Release(pointer: ref m_commandList);
        Release(pointer: ref m_commandAllocator);

        if (!m_fenceEvent.IsNull) {
            _ = PInvoke.CloseHandle(hObject: m_fenceEvent);
            m_fenceEvent = HANDLE.Null;
        }
    }
}
