using System.Runtime.Versioning;
using Puck.DirectX.Apis;
using Puck.DirectX.Interop;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.System.Com;

namespace Puck.DirectX;

// One fence for the device generation, independent of any residency's upload ring.
[SupportedOSPlatform("windows10.0.10240")]
internal sealed unsafe class DirectXQueueTimeline : GpuQueueTimeline {
    private readonly DirectXDeviceContext m_context;
    private readonly nint m_fence;

    internal DirectXQueueTimeline(DirectXDeviceContext context) {
        m_context = context;
        ((ID3D12Device*)context.Device.Handle)->CreateFence(Flags: default, InitialValue: 0, ppFence: out ID3D12Fence* fence);
        m_fence = (nint)fence;
    }

    protected override void Signal(ulong value) => DirectXCommandCalls.Signal(
        calls: DirectXDeviceCommandCalls.Of(deviceContext: m_context),
        // The queue door splits an open batch before the checkpoint is signaled.
        queue: (ID3D12CommandQueue*)m_context.CommandQueueHandle,
        fence: (ID3D12Fence*)m_fence, value: value);
    protected override ulong ReadCompleted() => DirectXSharedFence.ReadCompletedValue(fence: (ID3D12Fence*)m_fence);
    protected override void DisposeNative() => _ = ((IUnknown*)m_fence)->Release();
}
