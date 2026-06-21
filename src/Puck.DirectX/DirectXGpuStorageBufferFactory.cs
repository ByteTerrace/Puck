using System.Runtime.Versioning;
using Puck.DirectX.Interfaces;
using Puck.DirectX.Interop;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.Graphics.Dxgi.Common;

namespace Puck.DirectX;

/// <summary>
/// Implements <see cref="IGpuStorageBufferFactory"/> for Direct3D 12 by creating an upload-heap buffer that
/// is permanently mapped for host writes.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXGpuStorageBufferFactory : IGpuStorageBufferFactory {
    /// <inheritdoc/>
    public IGpuStorageBuffer Create(IGpuDeviceContext deviceContext, ulong sizeBytes) {
        var device = (ID3D12Device*)((IDirectXDeviceContext)deviceContext).Device.Handle;
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
            SampleDesc = new Windows.Win32.Graphics.Dxgi.Common.DXGI_SAMPLE_DESC { Count = 1, },
            Width = sizeBytes,
        };

        void* buffer;
        var resourceIid = ID3D12Resource.IID_Guid;

        device->CreateCommittedResource(
            in heapProperties,
            D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE,
            in bufferDesc,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_GENERIC_READ,
            (D3D12_CLEAR_VALUE?)null,
            in resourceIid,
            &buffer
        );

        void* mapped;

        ((ID3D12Resource*)buffer)->Map(0, (D3D12_RANGE*)null, &mapped);

        return new DirectXGpuStorageBuffer(
            bufferHandle: (nint)buffer,
            mapped: mapped,
            sizeBytes: sizeBytes
        );
    }

    /// <inheritdoc/>
    public IGpuStorageBuffer CreateDeviceLocal(IGpuDeviceContext deviceContext, ulong sizeBytes) {
        var device = (ID3D12Device*)((IDirectXDeviceContext)deviceContext).Device.Handle;
        // A default-heap buffer that allows unordered access: the GPU writes it (the beam prepass UAV); D3D12 forbids
        // UAVs on the upload heap that Create uses, so the GPU-written cull buffer needs its own default-heap resource.
        var heapProperties = new D3D12_HEAP_PROPERTIES {
            Type = D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT,
        };
        var bufferDesc = new D3D12_RESOURCE_DESC {
            DepthOrArraySize = 1,
            Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_BUFFER,
            Flags = D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS,
            Format = DXGI_FORMAT.DXGI_FORMAT_UNKNOWN,
            Height = 1,
            Layout = D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_ROW_MAJOR,
            MipLevels = 1,
            SampleDesc = new Windows.Win32.Graphics.Dxgi.Common.DXGI_SAMPLE_DESC { Count = 1, },
            Width = sizeBytes,
        };

        void* buffer;
        var resourceIid = ID3D12Resource.IID_Guid;

        device->CreateCommittedResource(
            in heapProperties,
            D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE,
            in bufferDesc,
            // D3D12 ignores the initial state for buffers (they are always created in COMMON and promoted to
            // UNORDERED_ACCESS implicitly on the beam prepass's first UAV write); passing COMMON avoids the
            // debug-layer warning that an UNORDERED_ACCESS initial state triggers.
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON,
            (D3D12_CLEAR_VALUE?)null,
            in resourceIid,
            &buffer
        );

        return new DirectXGpuStorageBuffer(
            bufferHandle: (nint)buffer,
            mapped: null,
            sizeBytes: sizeBytes
        );
    }

    /// <inheritdoc/>
    public IGpuStorageBuffer CreateIndirectArgs(IGpuDeviceContext deviceContext, ulong sizeBytes, bool deviceLocal = false) {
        // Device-local: a default-heap ALLOW_UNORDERED_ACCESS buffer a compute shader writes (then the caller barriers
        // UAV -> INDIRECT_ARGUMENT before ExecuteIndirect). Host-visible (default): an upload-heap buffer, already a
        // legal ExecuteIndirect source — its GENERIC_READ creation state includes INDIRECT_ARGUMENT and upload
        // resources never leave it, so no buffer-state transition is needed. Neither needs an extra D3D12 buffer flag.
        return deviceLocal
            ? CreateDeviceLocal(deviceContext: deviceContext, sizeBytes: sizeBytes)
            : Create(deviceContext: deviceContext, sizeBytes: sizeBytes);
    }
}
