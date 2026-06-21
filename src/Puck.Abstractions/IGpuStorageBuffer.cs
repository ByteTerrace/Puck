namespace Puck.Abstractions;

/// <summary>
/// A backend-neutral storage buffer handle, with host-visible write access.
/// </summary>
public interface IGpuStorageBuffer : IDisposable {
    /// <summary>Gets the native buffer handle.</summary>
    nint BufferHandle { get; }
    /// <summary>Gets the size, in bytes, of the buffer.</summary>
    ulong SizeBytes { get; }

    /// <summary>Maps the buffer, copies the supplied data into it from the start, and unmaps it.</summary>
    /// <typeparam name="T">The unmanaged element type of the data.</typeparam>
    /// <param name="data">The data to copy into the buffer.</param>
    void Write<T>(ReadOnlySpan<T> data) where T : unmanaged;
}
