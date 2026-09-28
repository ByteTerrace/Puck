using System.Runtime.Versioning;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.System.Com;

namespace Puck.DirectX.Interop;

/// <summary>
/// A Direct3D 12 readback-heap buffer implementing <see cref="IGpuReadbackBuffer"/>: permanently mapped, and permanently in
/// <c>COPY_DEST</c>, which the resource-state tracker starts it at, so no transition ever moves it.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXGpuReadbackBuffer : IGpuReadbackBuffer {
    private nint m_buffer;
    private void* m_mapped;
    private bool m_disposed;

    /// <summary>Initializes a new instance of the <see cref="DirectXGpuReadbackBuffer"/> class, taking ownership of an
    /// already-created, mapped readback-heap buffer.</summary>
    /// <param name="bufferHandle">The native <c>ID3D12Resource</c>; ownership moves to the new instance.</param>
    /// <param name="sizeBytes">The buffer's size, in bytes.</param>
    /// <param name="mapped">The buffer's persistent mapping.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bufferHandle"/> is zero.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="mapped"/> is <see langword="null"/>.</exception>
    public DirectXGpuReadbackBuffer(nint bufferHandle, ulong sizeBytes, void* mapped) {
        ArgumentOutOfRangeException.ThrowIfZero(value: bufferHandle);

        if (mapped is null) {
            throw new ArgumentNullException(
                paramName: nameof(mapped),
                message: "A readback buffer requires a valid persistent mapping."
            );
        }

        m_buffer = bufferHandle;
        DirectXResourceStates.Register(
            resource: m_buffer,
            state: D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST
        );
        m_mapped = mapped;
        SizeBytes = sizeBytes;
    }

    /// <inheritdoc/>
    public nint BufferHandle => m_buffer;
    /// <inheritdoc/>
    public ulong SizeBytes { get; }

    /// <inheritdoc/>
    public void Read(Span<byte> destination) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            other: SizeBytes,
            value: ((ulong)destination.Length)
        );
        new ReadOnlySpan<byte>(
            length: destination.Length,
            pointer: m_mapped
        ).CopyTo(destination: destination);
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
            _ = ((IUnknown*)m_buffer)->Release();
            m_buffer = 0;
        }
    }
}
