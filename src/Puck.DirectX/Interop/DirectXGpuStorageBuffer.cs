using System.Runtime.Versioning;
using Windows.Win32.System.Com;

namespace Puck.DirectX.Interop;

/// <summary>
/// A Direct3D 12 upload-heap buffer implementing <see cref="IGpuStorageBuffer"/>. Permanently mapped for
/// host writes; <see cref="Write{T}"/> copies data into it without mapping/unmapping overhead.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXGpuStorageBuffer : IGpuStorageBuffer {
    private nint m_buffer;
    private void* m_mapped;
    private bool m_disposed;

    /// <summary>Initializes a new instance taking ownership of an already-created upload-heap buffer.</summary>
    public DirectXGpuStorageBuffer(nint bufferHandle, ulong sizeBytes, void* mapped) {
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
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);

        var destination = new Span<T>(pointer: m_mapped, length: (int)(SizeBytes / (ulong)sizeof(T)));

        data.CopyTo(destination: destination);
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
