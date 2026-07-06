using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.DirectX.Interfaces;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.System.Com;
using static Puck.DirectX.DirectXConstants;

namespace Puck.DirectX.Interop;

/// <summary>
/// A Direct3D 12 offscreen render target implementing <see cref="IGpuRenderTarget"/>. Owns a default-heap
/// render-target texture, a non-shader-visible RTV heap, a shader-visible SRV heap (for
/// <see cref="ImageViewHandle"/>), and a command allocator/list pair packed as a GCHandle token for
/// <see cref="CommandBufferHandle"/>. The initial resource state is
/// <c>D3D12_RESOURCE_STATE_RENDER_TARGET</c>; <c>BeginRenderPass</c> transitions back from
/// <c>PIXEL_SHADER_RESOURCE</c> on frames after the first.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXGpuRenderTarget : IGpuRenderTarget {
    private readonly IDirectXDeviceContext m_deviceContext;
    private readonly GCHandle m_commandBufferToken;
    private readonly GCHandle m_imageViewToken;
    private bool m_disposed;
    private nint m_renderTarget;
    private nint m_rtvHeap;
    private nint m_srvHeap;

    /// <summary>Initializes and allocates all D3D12 objects for the offscreen target.</summary>
    public DirectXGpuRenderTarget(IDirectXDeviceContext deviceContext, DXGI_FORMAT format, uint width, uint height) {
        ArgumentNullException.ThrowIfNull(deviceContext);

        m_deviceContext = deviceContext;
        Width = width;
        Height = height;

        var device = (ID3D12Device*)deviceContext.Device.Handle;

        CreateRenderTarget(device, format, width, height);

        var heapDesc = new D3D12_DESCRIPTOR_HEAP_DESC {
            NumDescriptors = 1,
            Type = D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV,
            Flags = D3D12_DESCRIPTOR_HEAP_FLAGS.D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE,
        };

        device->CreateDescriptorHeap(
            pDescriptorHeapDesc: in heapDesc,
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
            DestDescriptor: GetCpuHeapStart((ID3D12DescriptorHeap*)srvHeap)
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

        var state = new DirectXCommandBufferState {
            Allocator = (nint)commandAllocator,
            CommandList = (nint)commandList,
        };

        m_commandBufferToken = GCHandle.Alloc(state);

        var imageView = new DirectXImageView {
            Format = format,
            ResourceHandle = m_renderTarget,
        };

        m_imageViewToken = GCHandle.Alloc(imageView);
    }

    /// <inheritdoc/>
    public nint CommandBufferHandle => GCHandle.ToIntPtr(m_commandBufferToken);
    /// <inheritdoc/>
    public nint FramebufferHandle => (nint)GetCpuHeapStart((ID3D12DescriptorHeap*)m_rtvHeap).ptr;
    /// <inheritdoc/>
    public uint Height { get; }
    /// <inheritdoc/>
    public nint ImageHandle => m_renderTarget;
    /// <inheritdoc/>
    public nint ImageViewHandle => GCHandle.ToIntPtr(m_imageViewToken);
    /// <inheritdoc/>
    public nint RenderPassHandle => m_renderTarget;
    /// <inheritdoc/>
    public uint Width { get; }

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
    private void CreateRenderTarget(ID3D12Device* device, DXGI_FORMAT format, uint width, uint height) {
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
            D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE,
            in textureDesc,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET,
            clearValue,
            in resourceIid,
            &renderTarget
        );
        m_renderTarget = (nint)renderTarget;

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
            DestDescriptor: GetCpuHeapStart((ID3D12DescriptorHeap*)rtvHeap)
        );
    }

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;

        if (m_commandBufferToken.IsAllocated) {
            var state = (DirectXCommandBufferState)m_commandBufferToken.Target!;
            Release(ref state.CommandList);
            Release(ref state.Allocator);
            m_commandBufferToken.Free();
        }

        if (m_imageViewToken.IsAllocated) {
            m_imageViewToken.Free();
        }

        Release(ref m_srvHeap);
        Release(ref m_rtvHeap);
        Release(ref m_renderTarget);
    }
}
