namespace Puck.Abstractions.Gpu;

/// <summary>A backend-neutral readback buffer (<see cref="IGpuBufferFactory.CreateReadback"/>): a copy's destination on
/// the GPU, read by the host once the submission that copied into it has completed.</summary>
public interface IGpuReadbackBuffer : IGpuBuffer {
    /// <summary>Copies the buffer's bytes from <paramref name="sourceOffsetBytes"/> on into <paramref name="destination"/>
    /// through its mapping. The contents are what the latest completed copy wrote; a read while a copy into the buffer is
    /// in flight is undefined.</summary>
    /// <param name="destination">The bytes to fill; with the offset, within <see cref="IGpuBuffer.SizeBytes"/>.</param>
    /// <param name="sourceOffsetBytes">The byte of the buffer the first destination byte comes from.</param>
    /// <exception cref="ArgumentOutOfRangeException">The range reaches past the buffer.</exception>
    /// <exception cref="ObjectDisposedException">The buffer has been disposed.</exception>
    void Read(Span<byte> destination, ulong sourceOffsetBytes = 0UL);
}
