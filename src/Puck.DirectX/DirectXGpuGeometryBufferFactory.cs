using System.Runtime.Versioning;
using Puck.DirectX.Interfaces;
using Puck.DirectX.Interop;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.System.Com;

namespace Puck.DirectX;

/// <summary>
/// Implements <see cref="IGpuGeometryBufferFactory"/> for Direct3D 12: an upload-heap buffer, whose permanent
/// <c>GENERIC_READ</c> state already includes the vertex and index buffer states, filled with the data. A draw binds it
/// by its GPU virtual address (<see cref="DirectXGpuRecorder.BindVertexBuffer"/>), so the buffer needs no view of
/// its own.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXGpuGeometryBufferFactory : IGpuGeometryBufferFactory {
    /// <inheritdoc/>
    public IGpuBuffer Create(IGpuDeviceContext deviceContext, ReadOnlySpan<byte> data, GpuBufferUsage usage) {
        GpuBufferUsages.Validate(
            sizeBytes: data.Length,
            usage: usage
        );

        var device = ((ID3D12Device*)((IDirectXDeviceContext)deviceContext).Device.Handle);
        var sizeBytes = ((ulong)data.Length);
        var buffer = DirectXBuffers.CreateCommitted(
            device: device,
            heapType: D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_UPLOAD,
            initialState: D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_GENERIC_READ,
            sizeBytes: sizeBytes
        );
        void* mapped;

        try {
            buffer->Map(
                Subresource: 0,
                pReadRange: ((D3D12_RANGE*)null),
                ppData: &mapped
            );
        } catch {
            _ = ((IUnknown*)buffer)->Release();

            throw;
        }

        var geometry = new DirectXGpuStorageBuffer(
            bufferHandle: ((nint)buffer),
            mapped: mapped,
            sizeBytes: sizeBytes
        );

        geometry.Write(data: data);

        return geometry;
    }
}
