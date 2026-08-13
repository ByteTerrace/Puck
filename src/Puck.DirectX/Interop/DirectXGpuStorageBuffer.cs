using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32.System.Com;

namespace Puck.DirectX.Interop;

/// <summary>
/// A Direct3D 12 upload-heap buffer implementing <see cref="IGpuStorageBuffer"/>. Permanently mapped for
/// host writes; its <see cref="IGpuStorageBuffer"/> write operations copy data without mapping/unmapping overhead.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXGpuStorageBuffer : IGpuStorageBuffer {
    private nint m_buffer;
    private void* m_mapped;
    private bool m_disposed;

    /// <summary>Initializes a new instance taking ownership of an already-created upload-heap buffer.</summary>
    public DirectXGpuStorageBuffer(nint bufferHandle, ulong sizeBytes, void* mapped) {
        ArgumentOutOfRangeException.ThrowIfZero(value: bufferHandle);

        if (mapped is null) {
            throw new ArgumentNullException(paramName: nameof(mapped), message: "A host-visible storage buffer requires a valid persistent mapping.");
        }

        m_buffer = bufferHandle;
        m_mapped = mapped;
        SizeBytes = sizeBytes;
    }

    /// <inheritdoc/>
    public nint BufferHandle => m_buffer;
    /// <inheritdoc/>
    public ulong SizeBytes { get; }

    /// <inheritdoc/>
    public void Write<T>(ReadOnlySpan<T> data) where T : unmanaged {
        Write(data: data, destinationOffsetBytes: 0UL);
    }

    /// <inheritdoc/>
    public void Write<T>(ReadOnlySpan<T> data, ulong destinationOffsetBytes) where T : unmanaged {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);

        var size = ((ulong)data.Length * (ulong)sizeof(T));

        if ((destinationOffsetBytes > SizeBytes) || (size > (SizeBytes - destinationOffsetBytes))) {
            throw new ArgumentOutOfRangeException(
                message: "Data size plus destination offset exceeds storage buffer size.",
                paramName: nameof(data)
            );
        }

        var destination = new Span<byte>(pointer: ((byte*)m_mapped + destinationOffsetBytes), length: (int)(SizeBytes - destinationOffsetBytes));

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
            _ = ((IUnknown*)m_buffer)->Release();
            m_buffer = 0;
        }
    }
}

/// <summary>Owns a Direct3D 12 device-local buffer without exposing host-write operations.</summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXGpuDeviceBuffer : IGpuBuffer {
    private nint m_buffer;

    /// <summary>Initializes an owner for a device-local buffer.</summary>
    public DirectXGpuDeviceBuffer(nint bufferHandle, ulong sizeBytes) {
        ArgumentOutOfRangeException.ThrowIfZero(value: bufferHandle);
        m_buffer = bufferHandle;
        SizeBytes = sizeBytes;
    }

    /// <inheritdoc/>
    public nint BufferHandle => m_buffer;
    /// <inheritdoc/>
    public ulong SizeBytes { get; }

    /// <inheritdoc/>
    public void Dispose() {
        if (0 != m_buffer) {
            _ = ((IUnknown*)m_buffer)->Release();
            m_buffer = 0;
        }
    }
}
