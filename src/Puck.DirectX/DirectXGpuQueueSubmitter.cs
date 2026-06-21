using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.DirectX.Interfaces;
using Windows.Win32.Graphics.Direct3D12;

namespace Puck.DirectX;

/// <summary>
/// Implements <see cref="IGpuQueueSubmitter"/> for Direct3D 12. Each <c>commandBufferHandle</c> in the span is
/// a GCHandle token pointing to a <see cref="DirectXCommandBufferState"/>; the underlying command list is
/// extracted and passed to <c>ExecuteCommandLists</c>. <see cref="SubmitAndWait"/> additionally calls
/// <see cref="IGpuDeviceContext.WaitIdle"/> on the device context.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXGpuQueueSubmitter : IGpuQueueSubmitter {
    /// <inheritdoc/>
    public void Submit(IGpuDeviceContext deviceContext, ReadOnlySpan<nint> commandBufferHandles) =>
        Execute(deviceContext: deviceContext, commandBufferHandles: commandBufferHandles);

    /// <inheritdoc/>
    public void SubmitAndWait(IGpuDeviceContext deviceContext, ReadOnlySpan<nint> commandBufferHandles) {
        Execute(deviceContext: deviceContext, commandBufferHandles: commandBufferHandles);
        deviceContext.WaitIdle();
    }

    private static void Execute(IGpuDeviceContext deviceContext, ReadOnlySpan<nint> commandBufferHandles) {
        var dxContext = (IDirectXDeviceContext)deviceContext;
        var queue = (ID3D12CommandQueue*)dxContext.CommandQueueHandle;
        var lists = stackalloc ID3D12CommandList*[commandBufferHandles.Length];

        for (var i = 0; (i < commandBufferHandles.Length); i++) {
            var state = (DirectXCommandBufferState)GCHandle.FromIntPtr(commandBufferHandles[i]).Target!;

            lists[i] = (ID3D12CommandList*)state.CommandList;
        }

        queue->ExecuteCommandLists((uint)commandBufferHandles.Length, lists);
    }
}
