using System.Runtime.Versioning;
using Puck.DirectX.Interfaces;

namespace Puck.DirectX.Interop;

/// <summary>
/// Owns a vertex buffer resource and exposes the GPU address, size, and stride a vertex-buffer view needs;
/// releases the resource on disposal. The Direct3D 12 analog of a <c>VulkanVertexBuffer</c>.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed class DirectXVertexBuffer : IDisposable {
    private readonly IDirectXVertexBufferApi m_vertexBufferApi;
    private bool m_disposed;

    /// <summary>Initializes a new instance of the <see cref="DirectXVertexBuffer"/> class, taking ownership of a resource.</summary>
    /// <param name="bufferHandle">The native <c>ID3D12Resource</c> handle to own.</param>
    /// <param name="gpuVirtualAddress">The buffer's GPU virtual address.</param>
    /// <param name="sizeBytes">The size, in bytes, of the buffer.</param>
    /// <param name="strideBytes">The size, in bytes, of one vertex.</param>
    /// <param name="vertexBufferApi">The API used to release the resource on disposal.</param>
    /// <exception cref="ArgumentNullException"><paramref name="vertexBufferApi"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="bufferHandle"/> is zero.</exception>
    public DirectXVertexBuffer(
        nint bufferHandle,
        ulong gpuVirtualAddress,
        uint sizeBytes,
        uint strideBytes,
        IDirectXVertexBufferApi vertexBufferApi
    ) {
        ArgumentNullException.ThrowIfNull(vertexBufferApi);

        if (0 == bufferHandle) {
            throw new ArgumentException(
                message: "Vertex buffer handle must be non-zero.",
                paramName: nameof(bufferHandle)
            );
        }

        BufferHandle = bufferHandle;
        BufferLocation = gpuVirtualAddress;
        SizeBytes = sizeBytes;
        StrideBytes = strideBytes;
        m_vertexBufferApi = vertexBufferApi;
    }

    /// <summary>Gets the native <c>ID3D12Resource</c> handle, or zero once disposed.</summary>
    public nint BufferHandle { get; private set; }
    /// <summary>Gets the buffer's GPU virtual address — the vertex-buffer view's <c>BufferLocation</c>.</summary>
    public ulong BufferLocation { get; }
    /// <summary>Gets the size, in bytes, of the buffer.</summary>
    public uint SizeBytes { get; }
    /// <summary>Gets the size, in bytes, of one vertex.</summary>
    public uint StrideBytes { get; }

    /// <summary>Releases the owned resource. Safe to call more than once.</summary>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        m_vertexBufferApi.DestroyVertexBuffer(bufferHandle: BufferHandle);
        BufferHandle = 0;
    }
}
