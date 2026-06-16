using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.Security;
using Windows.Win32.System.Com;

namespace Puck.DirectX.Interop;

/// <summary>
/// A minimal, window-bound Direct3D 12 presenter: it owns a device, command queue, flip-model swap chain,
/// render-target views, a command list, and a fence, and exposes a single <see cref="RenderClear"/> that
/// clears the current back buffer to a color and presents it. This is the first building block of the
/// Direct3D 12 backend — the equivalent of <c>VulkanRenderer</c>'s swapchain plumbing, before any geometry
/// or shaders. Single-thread affine: create and drive it on the window's pump thread.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXSwapChainRenderer : IDisposable {
    private const uint AllSubresources = 0xFFFFFFFF;
    private const uint FrameCount = 2;
    // The vtable slot of ID3D12DescriptorHeap::GetCPUDescriptorHandleForHeapStart (after the three IUnknown
    // methods, ID3D12Object's five, ID3D12DeviceChild's one, and ID3D12Pageable's none).
    private const int GetCpuDescriptorHandleSlot = 9;

    private readonly nint[] m_renderTargets = new nint[FrameCount];
    private readonly nint m_windowHandle;
    private nint m_commandAllocator;
    private nint m_commandList;
    private nint m_commandQueue;
    private nint m_device;
    private bool m_disposed;
    private HANDLE m_fenceEvent;
    private nint m_fence;
    private ulong m_fenceValue;
    private uint m_height;
    private nint m_rtvHeap;
    private uint m_rtvStride;
    private nint m_swapChain;
    private uint m_width;

    /// <summary>Initializes a new instance of the <see cref="DirectXSwapChainRenderer"/> class bound to a window.</summary>
    /// <param name="windowHandle">The native Win32 window handle (<c>HWND</c>) to present to.</param>
    /// <param name="width">The initial back-buffer width in pixels.</param>
    /// <param name="height">The initial back-buffer height in pixels.</param>
    /// <param name="minimumFeatureLevel">The minimum Direct3D feature level the device must support.</param>
    /// <exception cref="ArgumentException"><paramref name="windowHandle"/> is zero, or a dimension is zero.</exception>
    /// <exception cref="DirectXException">A DXGI or Direct3D 12 call failed.</exception>
    public DirectXSwapChainRenderer(nint windowHandle, uint width, uint height, DirectXFeatureLevel minimumFeatureLevel) {
        if (0 == windowHandle) {
            throw new ArgumentException(
                message: "A non-zero window handle is required.",
                paramName: nameof(windowHandle)
            );
        }

        if (
            (0 == width) ||
            (0 == height)
        ) {
            throw new ArgumentException(message: "Back-buffer dimensions must be non-zero.");
        }

        FeatureLevel = minimumFeatureLevel;
        m_height = height;
        m_width = width;
        m_windowHandle = windowHandle;

        Initialize(minimumFeatureLevel: minimumFeatureLevel);
    }

    /// <summary>Gets the feature level the device was created with.</summary>
    public DirectXFeatureLevel FeatureLevel { get; }
    /// <summary>Gets the current back-buffer height in pixels.</summary>
    public uint Height => m_height;
    /// <summary>Gets the current back-buffer width in pixels.</summary>
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
    // ID3D12DescriptorHeap::GetCPUDescriptorHandleForHeapStart returns a struct by value, and the x64 COM
    // ABI returns such user-defined types through a hidden pointer parameter inserted after `this`. The
    // CsWin32-generated wrapper omits that parameter, so calling it mismarshals and corrupts memory. Invoke
    // the vtable slot directly with the correct return-by-pointer signature instead.
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
    private void CreateRenderTargets() {
        var device = (ID3D12Device*)m_device;
        var heap = (ID3D12DescriptorHeap*)m_rtvHeap;
        var swapChain = (IDXGISwapChain3*)m_swapChain;
        var handle = GetCpuHeapStart(heap: heap);

        for (var index = 0U; (index < FrameCount); index++) {
            void* buffer;
            var resourceIid = ID3D12Resource.IID_Guid;

            swapChain->GetBuffer(
                index,
                &resourceIid,
                &buffer
            );
            device->CreateRenderTargetView(
                pResource: (ID3D12Resource*)buffer,
                pDesc: null,
                DestDescriptor: handle
            );

            m_renderTargets[index] = (nint)buffer;
            handle.ptr += m_rtvStride;
        }
    }
    private void Initialize(DirectXFeatureLevel minimumFeatureLevel) {
        var factory = DxgiInterop.CreateFactory();

        try {
            void* device;
            var deviceIid = ID3D12Device.IID_Guid;

            PInvoke.D3D12CreateDevice(
                pAdapter: null,
                MinimumFeatureLevel: (D3D_FEATURE_LEVEL)minimumFeatureLevel,
                riid: deviceIid,
                ppDevice: &device
            ).ThrowIfFailed(operation: "D3D12CreateDevice");
            m_device = (nint)device;

            var queueDesc = new D3D12_COMMAND_QUEUE_DESC {
                Type = D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT,
            };

            ((ID3D12Device*)device)->CreateCommandQueue(
                pDesc: in queueDesc,
                riid: ID3D12CommandQueue.IID_Guid,
                ppCommandQueue: out var commandQueue
            );
            m_commandQueue = (nint)commandQueue;

            CreateSwapChain(factory: factory);

            var heapDesc = new D3D12_DESCRIPTOR_HEAP_DESC {
                NumDescriptors = FrameCount,
                Type = D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_RTV,
            };

            ((ID3D12Device*)device)->CreateDescriptorHeap(
                pDescriptorHeapDesc: in heapDesc,
                riid: ID3D12DescriptorHeap.IID_Guid,
                ppvHeap: out var rtvHeap
            );
            m_rtvHeap = (nint)rtvHeap;
            m_rtvStride = ((ID3D12Device*)device)->GetDescriptorHandleIncrementSize(DescriptorHeapType: D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_RTV);

            CreateRenderTargets();

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
            // Command lists are created in the recording state; close it so the first RenderClear can reset it.
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
        } finally {
            var factoryPointer = (nint)factory;

            Release(pointer: ref factoryPointer);
        }
    }
    private void CreateSwapChain(IDXGIFactory4* factory) {
        var description = new DXGI_SWAP_CHAIN_DESC1 {
            AlphaMode = DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_UNSPECIFIED,
            BufferCount = FrameCount,
            BufferUsage = DXGI_USAGE.DXGI_USAGE_RENDER_TARGET_OUTPUT,
            Format = DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM,
            Height = m_height,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, },
            Scaling = DXGI_SCALING.DXGI_SCALING_STRETCH,
            SwapEffect = DXGI_SWAP_EFFECT.DXGI_SWAP_EFFECT_FLIP_DISCARD,
            Width = m_width,
        };

        IDXGISwapChain1* swapChain1 = null;

        factory->CreateSwapChainForHwnd(
            pDevice: (IUnknown*)m_commandQueue,
            hWnd: (HWND)m_windowHandle,
            pDesc: in description,
            pFullscreenDesc: null,
            pRestrictToOutput: null,
            ppSwapChain: &swapChain1
        );

        try {
            IDXGISwapChain3* swapChain3 = null;
            var swapChainIid = IDXGISwapChain3.IID_Guid;

            ((IUnknown*)swapChain1)->QueryInterface(
                &swapChainIid,
                (void**)&swapChain3
            ).ThrowIfFailed(operation: "IDXGISwapChain1::QueryInterface(IDXGISwapChain3)");
            m_swapChain = (nint)swapChain3;
        } finally {
            _ = ((IUnknown*)swapChain1)->Release();
        }
    }
    private void ReleaseRenderTargets() {
        for (var index = 0; (index < m_renderTargets.Length); index++) {
            Release(pointer: ref m_renderTargets[index]);
        }
    }
    private void WaitForGpu() {
        var fence = (ID3D12Fence*)m_fence;
        var value = m_fenceValue;

        ((ID3D12CommandQueue*)m_commandQueue)->Signal(
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

    /// <summary>Clears the current back buffer to a color and presents it, blocking until the GPU is done.</summary>
    /// <param name="red">The red component, in the range [0, 1].</param>
    /// <param name="green">The green component, in the range [0, 1].</param>
    /// <param name="blue">The blue component, in the range [0, 1].</param>
    /// <param name="alpha">The alpha component, in the range [0, 1].</param>
    /// <exception cref="ObjectDisposedException">The renderer has been disposed.</exception>
    /// <exception cref="DirectXException">Presentation failed.</exception>
    public void RenderClear(float red, float green, float blue, float alpha) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        var allocator = (ID3D12CommandAllocator*)m_commandAllocator;
        var commandList = (ID3D12GraphicsCommandList*)m_commandList;
        var heap = (ID3D12DescriptorHeap*)m_rtvHeap;
        var swapChain = (IDXGISwapChain3*)m_swapChain;

        var frameIndex = swapChain->GetCurrentBackBufferIndex();
        var backBuffer = (ID3D12Resource*)m_renderTargets[frameIndex];

        allocator->Reset();
        commandList->Reset(
            pAllocator: allocator,
            pInitialState: null
        );

        var toRenderTarget = CreateTransition(
            after: D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET,
            before: D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PRESENT,
            resource: backBuffer
        );

        commandList->ResourceBarrier(
            1,
            &toRenderTarget
        );

        var renderTargetView = GetCpuHeapStart(heap: heap);

        renderTargetView.ptr += (nuint)(frameIndex * m_rtvStride);

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

        var toPresent = CreateTransition(
            after: D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PRESENT,
            before: D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET,
            resource: backBuffer
        );

        commandList->ResourceBarrier(
            1,
            &toPresent
        );
        commandList->Close();

        var executable = (ID3D12CommandList*)commandList;

        ((ID3D12CommandQueue*)m_commandQueue)->ExecuteCommandLists(
            1,
            &executable
        );
        swapChain->Present(
            1,
            default
        ).ThrowIfFailed(operation: "IDXGISwapChain::Present");

        WaitForGpu();
    }
    /// <summary>Resizes the swap chain's back buffers, rebuilding the render-target views.</summary>
    /// <param name="width">The new back-buffer width in pixels. A zero dimension (a minimized window) is ignored.</param>
    /// <param name="height">The new back-buffer height in pixels.</param>
    /// <exception cref="ObjectDisposedException">The renderer has been disposed.</exception>
    /// <exception cref="DirectXException">The swap-chain resize failed.</exception>
    public void Resize(uint width, uint height) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        if (
            (0 == width) ||
            (0 == height) ||
            ((width == m_width) && (height == m_height))
        ) {
            return;
        }

        WaitForGpu();
        ReleaseRenderTargets();

        ((IDXGISwapChain3*)m_swapChain)->ResizeBuffers(
            FrameCount,
            width,
            height,
            DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM,
            0
        );

        m_height = height;
        m_width = width;

        CreateRenderTargets();
    }

    /// <summary>Waits for the GPU to finish, then releases every owned Direct3D 12 and DXGI object. Safe to call more than once.</summary>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;

        if (
            (0 != m_commandQueue) &&
            (0 != m_fence)
        ) {
            WaitForGpu();
        }

        ReleaseRenderTargets();
        Release(pointer: ref m_fence);
        Release(pointer: ref m_commandList);
        Release(pointer: ref m_commandAllocator);
        Release(pointer: ref m_rtvHeap);
        Release(pointer: ref m_swapChain);
        Release(pointer: ref m_commandQueue);
        Release(pointer: ref m_device);

        if (!m_fenceEvent.IsNull) {
            _ = PInvoke.CloseHandle(hObject: m_fenceEvent);
            m_fenceEvent = HANDLE.Null;
        }
    }
}
