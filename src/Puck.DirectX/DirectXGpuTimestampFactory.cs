using System.Runtime.Versioning;
using Puck.DirectX.Interop;
using Puck.DirectX.Apis;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.System.Com;

namespace Puck.DirectX;

/// <summary>Creates timestamp heaps on the direct queue, using that queue's actual timestamp frequency.</summary>
/// <param name="deviceContext">The owning device context.</param>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXGpuTimestampFactory(DirectXDeviceContext deviceContext) : IGpuTimestampFactory {
    /// <inheritdoc/>
    public IGpuTimestampPool? Create(uint count, in GpuObjectName name) {
        ArgumentOutOfRangeException.ThrowIfZero(value: count);
        var queue = ((ID3D12CommandQueue*)deviceContext.CommandQueueHandle);
        var heap = DirectXCommandCalls.CreateTimestampHeap(calls: DirectXDeviceCommandCalls.Of(deviceContext: deviceContext),
            queue: queue, count: count, frequency: out var frequency);

        if (heap is null) { return null; }
        try {
            deviceContext.Services.Naming.Name(handle: ((nint)heap), kind: GpuObjectKind.TimestampPool, name: in name);
        } catch {
            _ = heap->Release();
            throw;
        }
        return new Pool(frequency: frequency, handle: ((nint)heap));
    }

    private sealed class Pool(nint handle, ulong frequency) : IGpuTimestampPool {
        private nint m_handle = handle;

        public double NanosecondsPerTick => (1_000_000_000.0 / frequency);
        public uint ValidBits => 64;

        public void Reset(nint command, uint first, uint count) { }
        public void Write(nint command, uint index) => List(command: command)->EndQuery(Index: index, Type: D3D12_QUERY_TYPE.D3D12_QUERY_TYPE_TIMESTAMP, pQueryHeap: ((ID3D12QueryHeap*)m_handle));
        public void Resolve(nint command, uint first, uint count, nint destination, ulong offset) =>
            List(command: command)->ResolveQueryData(AlignedDestinationBufferOffset: offset, NumQueries: count, StartIndex: first, Type: D3D12_QUERY_TYPE.D3D12_QUERY_TYPE_TIMESTAMP, pDestinationBuffer: ((ID3D12Resource*)destination), pQueryHeap: ((ID3D12QueryHeap*)m_handle));

        private static ID3D12GraphicsCommandList* List(nint command) => ((ID3D12GraphicsCommandList*)DirectXCommandBufferState.Decode(commandBufferHandle: command).CommandList);

        public void Dispose() {
            if (m_handle != 0) { _ = ((IUnknown*)m_handle)->Release(); m_handle = 0; }
        }
    }
}
