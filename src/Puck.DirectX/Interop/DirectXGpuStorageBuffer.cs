using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32.System.Com;

namespace Puck.DirectX.Interop;

/// <summary>
/// A Direct3D 12 upload-heap or GPU-upload-heap buffer implementing <see cref="IGpuStorageBuffer"/>. Permanently mapped
/// for host writes; its <see cref="IGpuStorageBuffer"/> write operations copy data without mapping/unmapping overhead.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXGpuStorageBuffer : IGpuStorageBuffer {
    private readonly GpuDeviceMemoryWork? m_memory;

    private nint m_buffer;
    private void* m_mapped;
    private bool m_disposed;

    /// <summary>Initializes a new instance taking ownership of an already-created, mapped upload-heap or GPU-upload-heap
    /// buffer.</summary>
    /// <param name="bufferHandle">The native <c>ID3D12Resource</c>; ownership moves to the new instance.</param>
    /// <param name="sizeBytes">The buffer's size, in bytes.</param>
    /// <param name="mapped">The buffer's persistent mapping.</param>
    /// <param name="memory">The device-local counts a GPU-upload-heap buffer was counted into, whose release this owner
    /// counts, or <see langword="null"/>; an upload-heap buffer was counted into none, so its release counts
    /// nothing.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bufferHandle"/> is zero.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="mapped"/> is <see langword="null"/>.</exception>
    public DirectXGpuStorageBuffer(nint bufferHandle, ulong sizeBytes, void* mapped, GpuDeviceMemoryWork? memory = null) {
        ArgumentOutOfRangeException.ThrowIfZero(value: bufferHandle);

        if (mapped is null) {
            throw new ArgumentNullException(
                paramName: nameof(mapped),
                message: "A host-visible storage buffer requires a valid persistent mapping."
            );
        }

        m_buffer = bufferHandle;
        DirectXResourceStates.Register(
            resource: m_buffer,
            state: DirectXGpuBufferFactory.HostVisibleState
        );
        m_mapped = mapped;
        m_memory = memory;
        SizeBytes = sizeBytes;
    }

    /// <inheritdoc/>
    public nint BufferHandle => m_buffer;
    /// <inheritdoc/>
    public ulong SizeBytes { get; }

    /// <inheritdoc/>
    public void Write<T>(ReadOnlySpan<T> data) where T : unmanaged {
        Write(
            data: data,
            destinationOffsetBytes: 0UL
        );
    }
    /// <inheritdoc/>
    public void Write<T>(ReadOnlySpan<T> data, ulong destinationOffsetBytes) where T : unmanaged {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        var size = (((ulong)data.Length) * ((ulong)sizeof(T)));

        if (
            (destinationOffsetBytes > SizeBytes) ||
            (size > (SizeBytes - destinationOffsetBytes))
        ) {
            throw new ArgumentOutOfRangeException(
                message: "Data size plus destination offset exceeds storage buffer size.",
                paramName: nameof(data)
            );
        }

        var destination = new Span<byte>(
            pointer: (((byte*)m_mapped) + destinationOffsetBytes),
            length: ((int)(SizeBytes - destinationOffsetBytes))
        );

        MemoryMarshal.AsBytes(span: data).CopyTo(destination: destination);
    }
    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        m_mapped = null;

        if (0 != m_buffer) {
            DirectXResourceStates.Forget(resource: m_buffer);
            DirectXDeviceMemory.CountReleased(
                memory: m_memory,
                resource: m_buffer
            );
            _ = ((IUnknown*)m_buffer)->Release();
            m_buffer = 0;
        }
    }
}
/// <summary>Owns a Direct3D 12 device-local buffer without exposing host-write operations.</summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXGpuDeviceBuffer : IGpuBuffer {
    private readonly GpuDeviceMemoryWork? m_memory;

    private nint m_buffer;

    /// <summary>Initializes an owner for a device-local buffer.</summary>
    /// <param name="bufferHandle">The native <c>ID3D12Resource</c>; ownership moves to the new instance.</param>
    /// <param name="sizeBytes">The buffer's size, in bytes.</param>
    /// <param name="memory">The device-local counts the buffer was counted into, whose release this owner counts, or
    /// <see langword="null"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bufferHandle"/> is zero.</exception>
    public DirectXGpuDeviceBuffer(nint bufferHandle, ulong sizeBytes, GpuDeviceMemoryWork? memory = null) {
        ArgumentOutOfRangeException.ThrowIfZero(value: bufferHandle);
        m_buffer = bufferHandle;
        m_memory = memory;
        SizeBytes = sizeBytes;
    }

    /// <inheritdoc/>
    public nint BufferHandle => m_buffer;
    /// <inheritdoc/>
    public ulong SizeBytes { get; }

    /// <inheritdoc/>
    public void Dispose() {
        if (0 != m_buffer) {
            DirectXDeviceMemory.CountReleased(
                memory: m_memory,
                resource: m_buffer
            );
            _ = ((IUnknown*)m_buffer)->Release();
            m_buffer = 0;
        }
    }
}
