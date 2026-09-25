using System.Runtime.Versioning;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.Graphics.Dxgi.Common;

namespace Puck.DirectX;

/// <summary>
/// Creates the backend's committed buffers. Every buffer the backend creates goes through <see cref="CreateCommitted"/>,
/// so every one is a row-major, single-sample, format-less buffer on a heap with no heap flags, and differs only in its
/// size, heap type, initial state and resource flags.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public static unsafe class DirectXBuffers {
    /// <summary>Creates a committed buffer.</summary>
    /// <param name="device">The device.</param>
    /// <param name="sizeBytes">The buffer's size in bytes.</param>
    /// <param name="heapType">The heap it lives on: <c>UPLOAD</c> (host writes), <c>READBACK</c> (host reads), or
    /// <c>DEFAULT</c> (device-local).</param>
    /// <param name="initialState">The state it is created in: <c>GENERIC_READ</c> on an upload heap, <c>COPY_DEST</c> on
    /// a readback heap, and otherwise <c>COMMON</c>.</param>
    /// <param name="flags">Its resource flags; <c>ALLOW_UNORDERED_ACCESS</c> for a buffer a shader writes.</param>
    /// <param name="memory">The device-local counts a <c>DEFAULT</c>-heap buffer joins, or <see langword="null"/>; the
    /// owner counts its release through <see cref="DirectXDeviceMemory.CountReleased"/>.</param>
    /// <returns>The buffer, owned by the caller.</returns>
    public static ID3D12Resource* CreateCommitted(ID3D12Device* device, ulong sizeBytes, D3D12_HEAP_TYPE heapType, D3D12_RESOURCE_STATES initialState, D3D12_RESOURCE_FLAGS flags = D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_NONE, GpuDeviceMemoryWork? memory = null) {
        var heapProperties = new D3D12_HEAP_PROPERTIES {
            Type = heapType,
        };
        var description = new D3D12_RESOURCE_DESC {
            DepthOrArraySize = 1,
            Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_BUFFER,
            Flags = flags,
            Format = DXGI_FORMAT.DXGI_FORMAT_UNKNOWN,
            Height = 1,
            Layout = D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_ROW_MAJOR,
            MipLevels = 1,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, },
            Width = sizeBytes,
        };
        var resourceIid = ID3D12Resource.IID_Guid;
        void* buffer;

        device->CreateCommittedResource(
            HeapFlags: D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE,
            InitialResourceState: initialState,
            pDesc: in description,
            pHeapProperties: in heapProperties,
            pOptimizedClearValue: ((D3D12_CLEAR_VALUE?)null),
            ppvResource: &buffer,
            riidResource: in resourceIid
        );

        DirectXDeviceMemory.CountAllocated(
            device: device,
            memory: memory,
            resource: ((ID3D12Resource*)buffer),
            role: ((heapType == D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT)
                ? GpuMemoryRole.DeviceLocal
                : GpuMemoryRole.HostVisible
            )
        );

        return ((ID3D12Resource*)buffer);
    }
}
