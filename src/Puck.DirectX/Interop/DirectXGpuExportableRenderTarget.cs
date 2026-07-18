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
/// A Direct3D 12 offscreen render target in <em>shared</em> GPU memory implementing
/// <see cref="IGpuExportableRenderTarget"/>. It owns a committed render-target texture created with the shared
/// heap flag, an NT handle to it (from <c>CreateSharedHandle</c>), an RTV heap, a shader-visible SRV heap (for
/// <see cref="ImageViewHandle"/>), and the command resources both to compose into it and to transition it to the
/// cross-API handoff state. Another backend on the same adapter (a Vulkan host) imports
/// <see cref="SharedHandle"/> and samples the texture without a CPU round-trip.
/// <para>
/// The texture is created in, and left after each <see cref="FinalizeForExport"/> in, the <c>COMMON</c> state —
/// the cross-API handoff state. The shared <see cref="DirectXGpuCommandRecorder"/> ends compose passes in
/// <c>PIXEL_SHADER_RESOURCE</c>, so <see cref="FinalizeForExport"/> records the final transition back to
/// <c>COMMON</c> and blocks until the GPU is done. Single-thread affine.
/// </para>
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXGpuExportableRenderTarget : IGpuExportableRenderTarget {
    private readonly IDirectXDeviceContext m_deviceContext;
    private readonly GCHandle m_commandBufferToken;
    private readonly GCHandle m_imageViewToken;
    private nint m_finalizeAllocator;
    private nint m_finalizeCommandList;
    private bool m_disposed;
    private nint m_fence;
    private HANDLE m_fenceEvent;
    private ulong m_fenceValue;
    private nint m_renderTarget;
    private nint m_rtvHeap;
    private HANDLE m_sharedHandle;
    private nint m_srvHeap;

    /// <summary>Initializes and allocates all D3D12 objects for the shared offscreen target.</summary>
    /// <param name="deviceContext">The shared device context whose device and queue the texture is created and finalized on.</param>
    /// <param name="format">The render-target pixel format.</param>
    /// <param name="width">The texture width in pixels.</param>
    /// <param name="height">The texture height in pixels.</param>
    /// <exception cref="ArgumentNullException"><paramref name="deviceContext"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A dimension is zero.</exception>
    /// <exception cref="DirectXException">A Direct3D 12 call failed.</exception>
    public DirectXGpuExportableRenderTarget(IDirectXDeviceContext deviceContext, DXGI_FORMAT format, uint width, uint height) {
        ArgumentNullException.ThrowIfNull(deviceContext);

        if (
            (0 == width) ||
            (0 == height)
        ) {
            throw new ArgumentException(message: "Exportable render target dimensions must be non-zero.");
        }

        m_deviceContext = deviceContext;
        Width = width;
        Height = height;

        var device = (ID3D12Device*)deviceContext.Device.Handle;

        CreateSharedRenderTarget(device: device, format: format, height: height, width: width);

        var srvHeapDesc = new D3D12_DESCRIPTOR_HEAP_DESC {
            Flags = D3D12_DESCRIPTOR_HEAP_FLAGS.D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE,
            NumDescriptors = 1,
            Type = D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV,
        };

        device->CreateDescriptorHeap(
            pDescriptorHeapDesc: in srvHeapDesc,
            riid: ID3D12DescriptorHeap.IID_Guid,
            ppvHeap: out var srvHeap
        );
        m_srvHeap = (nint)srvHeap;

        var srvDesc = new D3D12_SHADER_RESOURCE_VIEW_DESC {
            Format = format,
            Shader4ComponentMapping = DefaultShader4ComponentMapping,
            ViewDimension = D3D12_SRV_DIMENSION.D3D12_SRV_DIMENSION_TEXTURE2D,
        };

        srvDesc.Anonymous.Texture2D = new D3D12_TEX2D_SRV { MipLevels = 1, };

        device->CreateShaderResourceView(
            pResource: (ID3D12Resource*)m_renderTarget,
            pDesc: &srvDesc,
            DestDescriptor: GetCpuHeapStart(heap: (ID3D12DescriptorHeap*)srvHeap)
        );

        device->CreateCommandAllocator(
            type: D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT,
            riid: ID3D12CommandAllocator.IID_Guid,
            ppCommandAllocator: out var commandAllocator
        );

        device->CreateCommandList(
            nodeMask: 0,
            type: D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT,
            pCommandAllocator: (ID3D12CommandAllocator*)commandAllocator,
            pInitialState: null,
            riid: ID3D12GraphicsCommandList.IID_Guid,
            ppCommandList: out var commandList
        );
        ((ID3D12GraphicsCommandList*)commandList)->Close();

        // The texture starts in COMMON, so seed the recorder's tracked state to match: the first compose pass
        // then transitions COMMON -> RENDER_TARGET correctly.
        var state = new DirectXCommandBufferState {
            Allocator = (nint)commandAllocator,
            CommandList = (nint)commandList,
            RenderTargetState = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON,
        };

        m_commandBufferToken = GCHandle.Alloc(value: state);

        var imageView = new DirectXImageView {
            Format = format,
            ResourceHandle = m_renderTarget,
        };

        m_imageViewToken = GCHandle.Alloc(value: imageView);

        device->CreateCommandAllocator(
            type: D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT,
            riid: ID3D12CommandAllocator.IID_Guid,
            ppCommandAllocator: out var finalizeAllocator
        );
        m_finalizeAllocator = (nint)finalizeAllocator;

        device->CreateCommandList(
            nodeMask: 0,
            type: D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT,
            pCommandAllocator: (ID3D12CommandAllocator*)finalizeAllocator,
            pInitialState: null,
            riid: ID3D12GraphicsCommandList.IID_Guid,
            ppCommandList: out var finalizeCommandList
        );
        m_finalizeCommandList = (nint)finalizeCommandList;
        ((ID3D12GraphicsCommandList*)finalizeCommandList)->Close();

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
    public nint CommandBufferHandle => GCHandle.ToIntPtr(value: m_commandBufferToken);
    /// <inheritdoc/>
    public nint FramebufferHandle => (nint)GetCpuHeapStart(heap: (ID3D12DescriptorHeap*)m_rtvHeap).ptr;
    /// <inheritdoc/>
    public uint Height { get; }
    /// <inheritdoc/>
    public nint ImageHandle => m_renderTarget;
    /// <inheritdoc/>
    public nint ImageViewHandle => GCHandle.ToIntPtr(value: m_imageViewToken);
    /// <inheritdoc/>
    public nint RenderPassHandle => m_renderTarget;
    /// <inheritdoc/>
    public nint SharedHandle => m_sharedHandle;
    /// <inheritdoc/>
    public uint Width { get; }

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
    private static void Release(ref nint pointer) {
        if (0 != pointer) {
            _ = ((IUnknown*)pointer)->Release();
            pointer = 0;
        }
    }
    // The optimized clear value (black, opaque) the render-target texture is created with, matching the recorder's
    // per-frame ClearRenderTargetView so the fast-clear path is taken instead of a slow generic clear.
    private static D3D12_CLEAR_VALUE BlackClearValue(DXGI_FORMAT format) {
        var clearValue = new D3D12_CLEAR_VALUE {
            Format = format,
        };

        clearValue.Anonymous.Color[3] = 1f;

        return clearValue;
    }
    private void CreateSharedRenderTarget(ID3D12Device* device, DXGI_FORMAT format, uint width, uint height) {
        var heapProperties = new D3D12_HEAP_PROPERTIES {
            Type = D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT,
        };
        var textureDesc = new D3D12_RESOURCE_DESC {
            DepthOrArraySize = 1,
            Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_TEXTURE2D,
            Flags = D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET,
            Format = format,
            Height = height,
            Layout = D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_UNKNOWN,
            MipLevels = 1,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, },
            Width = width,
        };

        void* renderTarget;
        var resourceIid = ID3D12Resource.IID_Guid;
        // An optimized clear value matching the per-frame ClearRenderTargetView (black, opaque) so the fast-clear path
        // is used and the debug layer does not warn that no clear value was supplied at creation.
        var clearValue = BlackClearValue(format: format);

        device->CreateCommittedResource(
            in heapProperties,
            D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_SHARED,
            in textureDesc,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON,
            clearValue,
            in resourceIid,
            &renderTarget
        );
        m_renderTarget = (nint)renderTarget;

        var sharedHandle = default(HANDLE);

        device->CreateSharedHandle(
            pObject: (ID3D12DeviceChild*)renderTarget,
            pAttributes: (SECURITY_ATTRIBUTES*)null,
            Access: GenericAll,
            Name: default(PCWSTR),
            pHandle: &sharedHandle
        );
        m_sharedHandle = sharedHandle;

        var rtvHeapDesc = new D3D12_DESCRIPTOR_HEAP_DESC {
            NumDescriptors = 1,
            Type = D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_RTV,
        };

        device->CreateDescriptorHeap(
            pDescriptorHeapDesc: in rtvHeapDesc,
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

        var state = (DirectXCommandBufferState)m_commandBufferToken.Target!;

        if (D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON != state.RenderTargetState) {
            var allocator = (ID3D12CommandAllocator*)m_finalizeAllocator;
            var commandList = (ID3D12GraphicsCommandList*)m_finalizeCommandList;

            allocator->Reset();
            commandList->Reset(
                pAllocator: allocator,
                pInitialState: null
            );

            var toCommon = CreateTransition(
                after: D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON,
                before: state.RenderTargetState,
                resource: (ID3D12Resource*)m_renderTarget
            );

            commandList->ResourceBarrier(1, &toCommon);
            commandList->Close();

            var executable = (ID3D12CommandList*)commandList;

            ((ID3D12CommandQueue*)m_deviceContext.CommandQueueHandle)->ExecuteCommandLists(1, &executable);

            state.RenderTargetState = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON;
        }

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

        if (m_commandBufferToken.IsAllocated) {
            var state = (DirectXCommandBufferState)m_commandBufferToken.Target!;

            Release(pointer: ref state.CommandList);
            Release(pointer: ref state.Allocator);
            m_commandBufferToken.Free();
        }

        if (m_imageViewToken.IsAllocated) {
            m_imageViewToken.Free();
        }

        Release(pointer: ref m_finalizeCommandList);
        Release(pointer: ref m_finalizeAllocator);
        Release(pointer: ref m_fence);
        Release(pointer: ref m_srvHeap);
        Release(pointer: ref m_rtvHeap);
        Release(pointer: ref m_renderTarget);

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
