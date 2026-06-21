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
/// A windowless Direct3D 12 render target in <em>shared</em> GPU memory: it owns a committed resource created
/// with the shared heap flag, an NT handle to it (from <c>CreateSharedHandle</c>), and the command resources to
/// clear it. Another backend on the same adapter (a Vulkan host) can import the handle and sample the texture
/// without a CPU round-trip. The resource is left in the <c>COMMON</c> state — the cross-API handoff state —
/// after each render. Single-thread affine.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXSharedTexture : IDisposable {
    private readonly IDirectXDeviceContext m_deviceContext;
    private readonly DXGI_FORMAT m_format;
    private readonly uint m_height;
    private readonly uint m_width;
    private nint m_commandAllocator;
    private nint m_commandList;
    private bool m_disposed;
    private nint m_fence;
    private HANDLE m_fenceEvent;
    private ulong m_fenceValue;
    private nint m_renderTarget;
    private nint m_rtvHeap;
    private HANDLE m_sharedHandle;

    /// <summary>Initializes a new instance of the <see cref="DirectXSharedTexture"/> class.</summary>
    /// <param name="deviceContext">The shared device context whose device and queue the texture is created and cleared on.</param>
    /// <param name="format">The texture's DXGI format. The importing backend MUST view the shared handle with the matching format, or it reinterprets the pixels.</param>
    /// <param name="width">The texture width in pixels.</param>
    /// <param name="height">The texture height in pixels.</param>
    /// <exception cref="ArgumentNullException"><paramref name="deviceContext"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A dimension is zero.</exception>
    /// <exception cref="DirectXException">A Direct3D 12 call failed.</exception>
    public DirectXSharedTexture(IDirectXDeviceContext deviceContext, DXGI_FORMAT format, uint width, uint height) {
        ArgumentNullException.ThrowIfNull(deviceContext);

        if (
            (0 == width) ||
            (0 == height)
        ) {
            throw new ArgumentException(message: "Shared texture dimensions must be non-zero.");
        }

        m_deviceContext = deviceContext;
        m_format = format;
        m_height = height;
        m_width = width;

        Initialize();
    }

    /// <summary>Gets the texture's DXGI format.</summary>
    public DXGI_FORMAT Format => m_format;
    /// <summary>Gets the texture height in pixels.</summary>
    public uint Height => m_height;
    /// <summary>Gets the NT handle to the shared resource, for import by another backend.</summary>
    public nint SharedHandle => m_sharedHandle;
    /// <summary>Gets the texture width in pixels.</summary>
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
            _ = ((IUnknown*)pointer)->Release();
            pointer = 0;
        }
    }
    private void Initialize() {
        var device = (ID3D12Device*)m_deviceContext.Device.Handle;
        var heapProperties = new D3D12_HEAP_PROPERTIES {
            Type = D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT,
        };
        var textureDesc = new D3D12_RESOURCE_DESC {
            DepthOrArraySize = 1,
            Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_TEXTURE2D,
            Flags = D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET,
            Format = m_format,
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
            D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_SHARED,
            in textureDesc,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON,
            (D3D12_CLEAR_VALUE?)null,
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

    /// <summary>Clears the shared texture to a color, blocking until the GPU is done, and leaves it in the
    /// <c>COMMON</c> state for the consuming backend.</summary>
    /// <param name="red">The red component, in the range [0, 1].</param>
    /// <param name="green">The green component, in the range [0, 1].</param>
    /// <param name="blue">The blue component, in the range [0, 1].</param>
    /// <param name="alpha">The alpha component, in the range [0, 1].</param>
    /// <exception cref="ObjectDisposedException">The texture has been disposed.</exception>
    /// <exception cref="DirectXException">A Direct3D 12 call failed.</exception>
    public void RenderClear(float red, float green, float blue, float alpha) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        var allocator = (ID3D12CommandAllocator*)m_commandAllocator;
        var commandList = (ID3D12GraphicsCommandList*)m_commandList;
        var renderTarget = (ID3D12Resource*)m_renderTarget;

        allocator->Reset();
        commandList->Reset(
            pAllocator: allocator,
            pInitialState: null
        );

        var toRenderTarget = CreateTransition(
            after: D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET,
            before: D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON,
            resource: renderTarget
        );

        commandList->ResourceBarrier(
            1,
            &toRenderTarget
        );

        var renderTargetView = GetCpuHeapStart(heap: (ID3D12DescriptorHeap*)m_rtvHeap);

        commandList->OMSetRenderTargets(
            NumRenderTargetDescriptors: 1,
            pRenderTargetDescriptors: &renderTargetView,
            RTsSingleHandleToDescriptorRange: false,
            pDepthStencilDescriptor: null
        );

        var color = stackalloc float[4] { red, green, blue, alpha, };

        commandList->ClearRenderTargetView(
            renderTargetView,
            color,
            0,
            null
        );

        var toCommon = CreateTransition(
            after: D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON,
            before: D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET,
            resource: renderTarget
        );

        commandList->ResourceBarrier(
            1,
            &toCommon
        );
        commandList->Close();

        var executable = (ID3D12CommandList*)commandList;

        ((ID3D12CommandQueue*)m_deviceContext.CommandQueueHandle)->ExecuteCommandLists(
            1,
            &executable
        );
        WaitForGpu();
    }

    /// <summary>Waits for the GPU, then releases the resource, its views, the shared handle, and command resources. Safe to call more than once.</summary>
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

        Release(pointer: ref m_rtvHeap);
        Release(pointer: ref m_renderTarget);
        Release(pointer: ref m_fence);
        Release(pointer: ref m_commandList);
        Release(pointer: ref m_commandAllocator);

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
