using System.Runtime.Versioning;
using Puck.DirectX.Interop;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.System.Com;

namespace Puck.DirectX;

/// <summary>
/// Implements <see cref="IGpuBufferFactory"/> for Direct3D 12 on its device context. A host-visible buffer is an
/// upload-heap buffer, permanently mapped and permanently in <see cref="HostVisibleState"/>, which covers every read
/// state a vertex, index, constant, shader-resource or indirect-argument use needs, so no transition ever moves it. A
/// device-local buffer is a default-heap buffer created in <see cref="DeviceLocalState"/> that allows unordered access
/// when it declares <see cref="GpuBufferUsage.Storage"/>; a shader's write promotes it to <c>UNORDERED_ACCESS</c>, and
/// an indirect dispatch reading it records the transition into <c>INDIRECT_ARGUMENT</c>
/// (<see cref="DirectXBufferStates"/>). A draw binds a buffer by its GPU virtual address
/// (<see cref="DirectXGpuRecorder.BindVertexBuffer"/>), so no usage needs a view of its own.
/// </summary>
/// <param name="deviceContext">The device context every buffer is created on.</param>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXGpuBufferFactory(DirectXDeviceContext deviceContext) : IGpuBufferFactory {
    /// <summary>The state a device-local buffer is created in. Direct3D 12 creates every buffer in <c>COMMON</c>
    /// whatever it is asked for, and an <c>UNORDERED_ACCESS</c> request draws a debug-layer warning.</summary>
    public const D3D12_RESOURCE_STATES DeviceLocalState = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON;
    /// <summary>The state a host-visible (upload-heap) buffer is created in and never leaves.</summary>
    public const D3D12_RESOURCE_STATES HostVisibleState = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_GENERIC_READ;

    /// <inheritdoc/>
    public IGpuBuffer CreateDeviceLocal(ulong sizeBytes, GpuBufferUsage usage) {
        GpuBufferUsages.Validate(
            sizeBytes: sizeBytes,
            usage: usage
        );

        var buffer = DirectXBuffers.CreateCommitted(
            device: Device,
            flags: ((0 != (usage & GpuBufferUsage.Storage))
                ? D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS
                : D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_NONE
            ),
            heapType: D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT,
            initialState: DeviceLocalState,
            memory: deviceContext.Memory,
            sizeBytes: sizeBytes
        );

        return new DirectXGpuDeviceBuffer(
            bufferHandle: ((nint)buffer),
            memory: deviceContext.Memory,
            sizeBytes: sizeBytes
        );
    }
    /// <inheritdoc/>
    public IGpuStorageBuffer CreateHostVisible(ulong sizeBytes, GpuBufferUsage usage) {
        GpuBufferUsages.Validate(
            sizeBytes: sizeBytes,
            usage: usage
        );

        var buffer = DirectXBuffers.CreateCommitted(
            device: Device,
            heapType: D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_UPLOAD,
            initialState: HostVisibleState,
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

        return new DirectXGpuStorageBuffer(
            bufferHandle: ((nint)buffer),
            mapped: mapped,
            sizeBytes: sizeBytes
        );
    }
    /// <inheritdoc/>
    public IGpuStorageBuffer CreateHostVisible(ReadOnlySpan<byte> data, GpuBufferUsage usage) {
        var buffer = CreateHostVisible(
            sizeBytes: ((ulong)data.Length),
            usage: usage
        );

        buffer.Write(data: data);

        return buffer;
    }

    private ID3D12Device* Device =>
        ((ID3D12Device*)deviceContext.Device.Handle);
}
