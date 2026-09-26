using System.Runtime.Versioning;
using Puck.DirectX.Apis;
using Puck.DirectX.Interop;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.System.Com;

namespace Puck.DirectX;

/// <summary>
/// Implements <see cref="IGpuBufferFactory"/> for Direct3D 12 on its device context. A host-visible buffer is an
/// upload-heap buffer, permanently mapped and permanently in <see cref="HostVisibleState"/>, which covers every read
/// state a vertex, index, constant, shader-resource or indirect-argument use needs, so no transition ever moves it. A
/// host-visible device-local buffer is mapped the same way on a <c>GPU_UPLOAD</c> heap, the adapter's memory the host
/// writes through its aperture, which exists only where the device reports GPU upload heaps; like a default-heap
/// buffer it is created in <see cref="DeviceLocalState"/>, from which a read promotes it. A
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
    public IGpuBuffer CreateDeviceLocal(ulong sizeBytes, GpuBufferUsage usage, in GpuObjectName name) {
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

        Naming.Name(
            handle: ((nint)buffer),
            kind: GpuObjectKind.Buffer,
            name: in name
        );

        return new DirectXGpuDeviceBuffer(
            bufferHandle: ((nint)buffer),
            memory: deviceContext.Memory,
            sizeBytes: sizeBytes
        );
    }
    /// <inheritdoc/>
    public IGpuStorageBuffer CreateHostVisible(ulong sizeBytes, GpuBufferUsage usage, in GpuObjectName name) =>
        CreateMapped(
            heapType: D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_UPLOAD,
            name: in name,
            sizeBytes: sizeBytes,
            usage: usage
        );
    /// <inheritdoc/>
    public IGpuStorageBuffer CreateHostVisibleDeviceLocal(ulong sizeBytes, GpuBufferUsage usage, in GpuObjectName name) =>
        CreateMapped(
            heapType: D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_GPU_UPLOAD,
            name: in name,
            sizeBytes: sizeBytes,
            usage: usage
        );
    /// <inheritdoc/>
    public IGpuStorageBuffer CreateHostVisible(ReadOnlySpan<byte> data, GpuBufferUsage usage, in GpuObjectName name) {
        var buffer = CreateHostVisible(
            name: in name,
            sizeBytes: ((ulong)data.Length),
            usage: usage
        );

        buffer.Write(data: data);

        return buffer;
    }

    private ID3D12Device* Device =>
        ((ID3D12Device*)deviceContext.Device.Handle);
    private GpuObjectNaming Naming =>
        deviceContext.Services.Naming;

    // A permanently mapped buffer: on an UPLOAD heap in HostVisibleState, on a GPU_UPLOAD heap in DeviceLocalState (the
    // runtime creates every buffer outside an upload heap in COMMON), where it joins the device-local counts, since it
    // is the adapter's memory.
    private IGpuStorageBuffer CreateMapped(D3D12_HEAP_TYPE heapType, ulong sizeBytes, GpuBufferUsage usage, in GpuObjectName name) {
        GpuBufferUsages.Validate(
            sizeBytes: sizeBytes,
            usage: usage
        );

        var state = ((heapType == D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_UPLOAD)
            ? HostVisibleState
            : DeviceLocalState
        );
        var buffer = DirectXBuffers.CreateCommitted(
            device: Device,
            heapType: heapType,
            initialState: state,
            memory: deviceContext.Memory,
            sizeBytes: sizeBytes
        );
        void* mapped;

        try {
            mapped = DirectXCommandCalls.Map(
                calls: new DirectXDeviceCommandCalls(device: Device),
                resource: buffer
            );
        } catch {
            DirectXDeviceMemory.CountReleased(
                memory: deviceContext.Memory,
                resource: ((nint)buffer)
            );
            _ = ((IUnknown*)buffer)->Release();

            throw;
        }

        Naming.Name(
            handle: ((nint)buffer),
            kind: GpuObjectKind.Buffer,
            name: in name
        );

        return new DirectXGpuStorageBuffer(
            bufferHandle: ((nint)buffer),
            mapped: mapped,
            memory: deviceContext.Memory,
            sizeBytes: sizeBytes,
            state: state
        );
    }
}
