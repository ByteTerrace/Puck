namespace Puck.Abstractions.Gpu;

/// <summary>A backend-neutral readback buffer (<see cref="IGpuBufferFactory.CreateReadback"/>): a copy's destination on
/// the GPU, read by the host once the submission that copied into it has completed.</summary>
public interface IGpuReadbackBuffer : IGpuBuffer {
    /// <summary>Copies the buffer's first bytes into <paramref name="destination"/> through its mapping. The contents
    /// are what the latest completed copy wrote; a read while a copy into the buffer is in flight is undefined.</summary>
    /// <param name="destination">The bytes to fill, at most <see cref="IGpuBuffer.SizeBytes"/> long.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="destination"/> is longer than the buffer.</exception>
    /// <exception cref="ObjectDisposedException">The buffer has been disposed.</exception>
    void Read(Span<byte> destination);
}
