using System.Runtime.Versioning;
using Puck.DirectX.Interfaces;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.Graphics.Dxgi.Common;

namespace Puck.DirectX;

/// <summary>
/// Implements <see cref="IGpuTimingPoolFactory"/> for Direct3D 12: creates a timestamp <c>ID3D12QueryHeap</c> paired
/// with a READBACK buffer (resolved ticks land there), and reports the device's timestamp period from the command
/// queue's <c>GetTimestampFrequency</c>. Queries are core Direct3D 12 (no feature gate needed).
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXGpuTimingPoolFactory : IGpuTimingPoolFactory {
    /// <inheritdoc/>
    public IGpuTimingPool CreateTimestampPool(IGpuDeviceContext deviceContext, uint queryCapacity) {
        ArgumentNullException.ThrowIfNull(deviceContext);

        var device = (ID3D12Device*)((IDirectXDeviceContext)deviceContext).Device.Handle;
        var queryHeapDesc = new D3D12_QUERY_HEAP_DESC {
            Count = queryCapacity,
            NodeMask = 0,
            Type = D3D12_QUERY_HEAP_TYPE.D3D12_QUERY_HEAP_TYPE_TIMESTAMP,
        };

        void* queryHeap;
        var queryHeapIid = ID3D12QueryHeap.IID_Guid;

        device->CreateQueryHeap(
            in queryHeapDesc,
            in queryHeapIid,
            &queryHeap
        );

        // A READBACK-heap buffer the resolved ticks are copied into (one ulong per query), cloning the
        // DirectXGpuSurfaceReadback buffer pattern.
        var heapProperties = new D3D12_HEAP_PROPERTIES { Type = D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_READBACK };
        var bufferDesc = new D3D12_RESOURCE_DESC {
            DepthOrArraySize = 1,
            Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_BUFFER,
            Format = DXGI_FORMAT.DXGI_FORMAT_UNKNOWN,
            Height = 1,
            Layout = D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_ROW_MAJOR,
            MipLevels = 1,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, },
            Width = ((ulong)queryCapacity * sizeof(ulong)),
        };

        void* readbackBuffer;
        var resourceIid = ID3D12Resource.IID_Guid;

        device->CreateCommittedResource(
            in heapProperties,
            D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE,
            in bufferDesc,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST,
            (D3D12_CLEAR_VALUE?)null,
            in resourceIid,
            &readbackBuffer
        );

        return new DirectXGpuTimingPool(
            capacity: queryCapacity,
            queryHeapHandle: (nint)queryHeap,
            readbackBufferHandle: (nint)readbackBuffer
        );
    }

    /// <inheritdoc/>
    public GpuTimestampCapabilities GetCapabilities(IGpuDeviceContext deviceContext) {
        ArgumentNullException.ThrowIfNull(deviceContext);

        var queue = (ID3D12CommandQueue*)((IDirectXDeviceContext)deviceContext).CommandQueueHandle;

        queue->GetTimestampFrequency(out var frequency);

        // Direct3D 12 timestamps are full 64-bit; the period is the inverse of the queue tick frequency.
        return new GpuTimestampCapabilities(
            PeriodNanoseconds: ((frequency > 0UL) ? (1_000_000_000.0 / frequency) : 0.0),
            ValidBits: ((frequency > 0UL) ? 64u : 0u)
        );
    }
}
