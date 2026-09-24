using System.Runtime.Versioning;
using Puck.DirectX.Interfaces;
using Puck.DirectX.Interop;
using Windows.Win32.Graphics.Direct3D12;

namespace Puck.DirectX;

/// <summary>
/// Implements <see cref="IGpuStorageBufferFactory"/> for Direct3D 12 by creating an upload-heap buffer that
/// is permanently mapped for host writes.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXGpuStorageBufferFactory : IGpuStorageBufferFactory {
    /// <inheritdoc/>
    public IGpuStorageBuffer Create(IGpuDeviceContext deviceContext, ulong sizeBytes) {
        var device = ((ID3D12Device*)((IDirectXDeviceContext)deviceContext).Device.Handle);
        var buffer = DirectXBuffers.CreateCommitted(
            device: device,
            heapType: D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_UPLOAD,
            initialState: D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_GENERIC_READ,
            sizeBytes: sizeBytes
        );

        void* mapped;

        buffer->Map(
            Subresource: 0,
            pReadRange: ((D3D12_RANGE*)null),
            ppData: &mapped
        );

        return new DirectXGpuStorageBuffer(
            bufferHandle: ((nint)buffer),
            mapped: mapped,
            sizeBytes: sizeBytes
        );
    }
    /// <inheritdoc/>
    public IGpuBuffer CreateDeviceLocal(IGpuDeviceContext deviceContext, ulong sizeBytes) {
        var device = ((ID3D12Device*)((IDirectXDeviceContext)deviceContext).Device.Handle);
        // A default-heap buffer that allows unordered access: the GPU writes it (the beam prepass UAV); D3D12 forbids
        // UAVs on the upload heap that Create uses, so the GPU-written cull buffer needs its own default-heap resource.
        var buffer = DirectXBuffers.CreateCommitted(
            device: device,
            flags: D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS,
            heapType: D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT,
            // D3D12 ignores the initial state for buffers (they are always created in COMMON and promoted to
            // UNORDERED_ACCESS implicitly on the beam prepass's first UAV write); passing COMMON avoids the
            // debug-layer warning that an UNORDERED_ACCESS initial state triggers.
            initialState: D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON,
            sizeBytes: sizeBytes
        );

        return new DirectXGpuDeviceBuffer(
            bufferHandle: ((nint)buffer),
            sizeBytes: sizeBytes
        );
    }
    /// <inheritdoc/>
    public IGpuStorageBuffer CreateIndirectArgs(IGpuDeviceContext deviceContext, ulong sizeBytes) {
        // Device-local: a default-heap ALLOW_UNORDERED_ACCESS buffer a compute shader writes (then the caller barriers
        // UAV -> INDIRECT_ARGUMENT before ExecuteIndirect). Host-visible (default): an upload-heap buffer, already a
        // legal ExecuteIndirect source — its GENERIC_READ creation state includes INDIRECT_ARGUMENT and upload
        // resources never leave it, so no buffer-state transition is needed. Neither needs an extra D3D12 buffer flag.
        return Create(
            deviceContext: deviceContext,
            sizeBytes: sizeBytes
        );
    }
    /// <inheritdoc/>
    public IGpuBuffer CreateDeviceLocalIndirectArgs(IGpuDeviceContext deviceContext, ulong sizeBytes) =>
        CreateDeviceLocal(
            deviceContext: deviceContext,
            sizeBytes: sizeBytes
        );
}
