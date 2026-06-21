namespace Puck.Abstractions;

/// <summary>
/// A backend-neutral vertex buffer handle.
/// </summary>
public interface IGpuVertexBuffer : IDisposable {
    /// <summary>Gets the native buffer handle.</summary>
    nint BufferHandle { get; }
}
