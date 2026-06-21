using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32.System.Com;

namespace Puck.DirectX;

/// <summary>
/// Implements <see cref="IGpuTimingPool"/> for Direct3D 12: a <see cref="DirectXTimingPoolState"/> (timestamp query
/// heap + READBACK buffer) wrapped in a <see cref="GCHandle"/> whose pointer is the neutral <see cref="PoolHandle"/>.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXGpuTimingPool : IGpuTimingPool {
    private bool m_disposed;
    private GCHandle m_token;

    /// <summary>Initializes a new instance of the <see cref="DirectXGpuTimingPool"/> class.</summary>
    /// <param name="queryHeapHandle">The native <c>ID3D12QueryHeap</c> handle.</param>
    /// <param name="readbackBufferHandle">The native READBACK <c>ID3D12Resource</c> buffer the resolved ticks land in.</param>
    /// <param name="capacity">The number of timestamp queries the heap holds.</param>
    public DirectXGpuTimingPool(nint queryHeapHandle, nint readbackBufferHandle, uint capacity) {
        Capacity = capacity;
        m_token = GCHandle.Alloc(new DirectXTimingPoolState {
            Capacity = capacity,
            QueryHeapHandle = queryHeapHandle,
            ReadbackBufferHandle = readbackBufferHandle,
        });
    }

    /// <inheritdoc/>
    public uint Capacity { get; }
    /// <inheritdoc/>
    public nint PoolHandle => GCHandle.ToIntPtr(m_token);

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;

        if (m_token.IsAllocated) {
            var state = (DirectXTimingPoolState)m_token.Target!;

            if (0 != state.ReadbackBufferHandle) {
                _ = ((IUnknown*)state.ReadbackBufferHandle)->Release();
                state.ReadbackBufferHandle = 0;
            }

            if (0 != state.QueryHeapHandle) {
                _ = ((IUnknown*)state.QueryHeapHandle)->Release();
                state.QueryHeapHandle = 0;
            }

            m_token.Free();
        }
    }
}
