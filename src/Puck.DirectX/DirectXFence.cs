using System.Runtime.Versioning;
using Puck.DirectX.Interfaces;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D12;

namespace Puck.DirectX;

[SupportedOSPlatform("windows10.0.10240")]
internal static unsafe class DirectXFence {
    public static void SignalAndWait(IDirectXDeviceContext deviceContext, nint fenceHandle, HANDLE fenceEvent, ref ulong fenceValue) {
        var queue = ((ID3D12CommandQueue*)deviceContext.CommandQueueHandle);
        var fence = ((ID3D12Fence*)fenceHandle);
        var value = fenceValue;

        queue->Signal(
            Value: value,
            pFence: fence
        );
        fenceValue++;

        if (fence->GetCompletedValue() < value) {
            fence->SetEventOnCompletion(
                Value: value,
                hEvent: fenceEvent
            );
            _ = PInvoke.WaitForSingleObject(
                dwMilliseconds: uint.MaxValue,
                hHandle: fenceEvent
            );
        }
    }
}
