using System.Runtime.Versioning;
using Puck.DirectX.Interfaces;
using Puck.DirectX.Messages;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.System.Com;

namespace Puck.DirectX.Apis;

/// <summary>
/// The native implementation of <see cref="IDirectXVertexBufferApi"/>: it creates a committed upload-heap
/// buffer, maps it to copy the vertex bytes in, and exposes its GPU virtual address.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXNativeVertexBufferApi : IDirectXVertexBufferApi {
    /// <inheritdoc/>
    public DirectXVertexBufferCreateResult CreateVertexBuffer(DirectXVertexBufferCreateRequest request) {
        if (0 == request.DeviceHandle) {
            throw new ArgumentException(
                message: "Direct3D 12 device handle must be non-zero.",
                paramName: nameof(request)
            );
        }

        if (request.VertexData.IsEmpty) {
            throw new ArgumentException(
                message: "Vertex data must not be empty.",
                paramName: nameof(request)
            );
        }

        var device = (ID3D12Device*)request.DeviceHandle;
        var sizeBytes = (uint)request.VertexData.Length;
        var heapProperties = new D3D12_HEAP_PROPERTIES {
            Type = D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_UPLOAD,
        };
        var bufferDesc = new D3D12_RESOURCE_DESC {
            DepthOrArraySize = 1,
            Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_BUFFER,
            Format = DXGI_FORMAT.DXGI_FORMAT_UNKNOWN,
            Height = 1,
            Layout = D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_ROW_MAJOR,
            MipLevels = 1,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, },
            Width = sizeBytes,
        };

        void* resource;
        var resourceIid = ID3D12Resource.IID_Guid;

        device->CreateCommittedResource(
            in heapProperties,
            D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE,
            in bufferDesc,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_GENERIC_READ,
            (D3D12_CLEAR_VALUE?)null,
            in resourceIid,
            &resource
        );

        var buffer = (ID3D12Resource*)resource;

        try {
            void* mapped;

            buffer->Map(
                0,
                (D3D12_RANGE*)null,
                &mapped
            );
            request.VertexData.Span.CopyTo(destination: new Span<byte>(
                pointer: mapped,
                length: (int)sizeBytes
            ));
            buffer->Unmap(
                0,
                (D3D12_RANGE*)null
            );

            return new DirectXVertexBufferCreateResult(
                BufferHandle: (nint)resource,
                GpuVirtualAddress: buffer->GetGPUVirtualAddress(),
                SizeBytes: sizeBytes
            );
        } catch {
            // The committed resource is created; release it if mapping/copying the vertex bytes fails.
            _ = ((IUnknown*)resource)->Release();

            throw;
        }
    }
    /// <inheritdoc/>
    public void DestroyVertexBuffer(nint bufferHandle) {
        if (0 != bufferHandle) {
            _ = ((IUnknown*)bufferHandle)->Release();
        }
    }
}
