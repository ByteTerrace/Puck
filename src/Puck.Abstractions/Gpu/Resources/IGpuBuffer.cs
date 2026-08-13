namespace Puck.Abstractions.Gpu;

/// <summary>A backend-neutral owning GPU buffer that does not imply host visibility.</summary>
public interface IGpuBuffer : IDisposable {
    /// <summary>Gets the native buffer handle.</summary>
    nint BufferHandle { get; }
    /// <summary>Gets the size, in bytes, of the buffer.</summary>
    ulong SizeBytes { get; }
}
