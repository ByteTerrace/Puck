using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32.Graphics.Direct3D12;

namespace Puck.DirectX;

/// <summary>
/// Implements <see cref="IGpuTimingRecorder"/> for Direct3D 12. Timestamps are point-in-time <c>EndQuery</c> writes;
/// <see cref="ResetTimestamps"/> is a no-op (the resolve is destructive); <see cref="ResolveTimestamps"/> copies the
/// query heap into the pool's READBACK buffer; <see cref="ReadTimestamps"/> maps that buffer.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXGpuTimingRecorder : IGpuTimingRecorder {
    /// <inheritdoc/>
    public void ResetTimestamps(nint deviceHandle, nint commandBufferHandle, nint poolHandle, uint firstQuery, uint queryCount) {
        // Direct3D 12 has no per-query reset — ResolveQueryData overwrites the destination, so there is nothing to do.
    }

    /// <inheritdoc/>
    public void WriteTimestamp(nint deviceHandle, nint commandBufferHandle, nint poolHandle, uint queryIndex, GpuTimingStage stageFlags) {
        var commandList = (ID3D12GraphicsCommandList*)DecodeCommand(commandBufferHandle: commandBufferHandle).CommandList;
        var pool = DecodePool(poolHandle: poolHandle);

        // A timestamp is written with EndQuery (it has no BeginQuery); the neutral stage is point-in-time on D3D12.
        commandList->EndQuery(
            (ID3D12QueryHeap*)pool.QueryHeapHandle,
            D3D12_QUERY_TYPE.D3D12_QUERY_TYPE_TIMESTAMP,
            queryIndex
        );
    }

    /// <inheritdoc/>
    public void ResolveTimestamps(nint deviceHandle, nint commandBufferHandle, nint poolHandle, uint firstQuery, uint queryCount) {
        var commandList = (ID3D12GraphicsCommandList*)DecodeCommand(commandBufferHandle: commandBufferHandle).CommandList;
        var pool = DecodePool(poolHandle: poolHandle);

        commandList->ResolveQueryData(
            (ID3D12QueryHeap*)pool.QueryHeapHandle,
            D3D12_QUERY_TYPE.D3D12_QUERY_TYPE_TIMESTAMP,
            firstQuery,
            queryCount,
            (ID3D12Resource*)pool.ReadbackBufferHandle,
            (firstQuery * (ulong)sizeof(ulong))
        );
    }

    /// <inheritdoc/>
    public uint ReadTimestamps(nint deviceHandle, nint poolHandle, uint firstQuery, uint queryCount, Span<ulong> rawTicks) {
        if ((uint)rawTicks.Length < queryCount) {
            return 0u;
        }

        var pool = DecodePool(poolHandle: poolHandle);
        var buffer = (ID3D12Resource*)pool.ReadbackBufferHandle;
        // Map the resolved-tick range for CPU read (the pointer is to the buffer base regardless of the read range).
        var readRange = new D3D12_RANGE {
            Begin = (nuint)(firstQuery * (ulong)sizeof(ulong)),
            End = (nuint)((firstQuery + queryCount) * (ulong)sizeof(ulong)),
        };

        void* mapped;

        buffer->Map(0, &readRange, &mapped);

        try {
            var ticks = (ulong*)mapped;

            for (var index = 0u; (index < queryCount); index++) {
                rawTicks[(int)index] = ticks[firstQuery + index];
            }
        } finally {
            // We wrote nothing back; an empty written range tells the runtime so.
            var writtenRange = new D3D12_RANGE { Begin = 0, End = 0, };

            buffer->Unmap(0, &writtenRange);
        }

        return queryCount;
    }

    private static DirectXCommandBufferState DecodeCommand(nint commandBufferHandle) =>
        (DirectXCommandBufferState)GCHandle.FromIntPtr(commandBufferHandle).Target!;
    private static DirectXTimingPoolState DecodePool(nint poolHandle) =>
        (DirectXTimingPoolState)GCHandle.FromIntPtr(poolHandle).Target!;
}
