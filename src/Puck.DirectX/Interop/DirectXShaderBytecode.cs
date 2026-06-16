using System.Runtime.Versioning;
using Windows.Win32.Graphics.Direct3D;

namespace Puck.DirectX.Interop;

/// <summary>
/// Owns a compiled shader bytecode blob (an <c>ID3DBlob</c>), exposing the pointer and length a pipeline needs
/// to embed it, and releasing it on disposal. The Direct3D 12 analog of a <c>VulkanShaderModule</c> — except a
/// PSO copies the bytecode at creation, so the blob may be disposed once the pipeline is built.
/// </summary>
[SupportedOSPlatform("windows8.1")]
public sealed unsafe class DirectXShaderBytecode : IDisposable {
    private nint m_blobHandle;

    /// <summary>Initializes a new instance of the <see cref="DirectXShaderBytecode"/> class taking ownership of a blob.</summary>
    /// <param name="blobHandle">The native <c>ID3DBlob</c> pointer to own.</param>
    /// <exception cref="ArgumentException"><paramref name="blobHandle"/> is zero.</exception>
    public DirectXShaderBytecode(nint blobHandle) {
        if (0 == blobHandle) {
            throw new ArgumentException(
                message: "Shader bytecode blob handle must be non-zero.",
                paramName: nameof(blobHandle)
            );
        }

        m_blobHandle = blobHandle;
    }

    /// <summary>Gets the length, in bytes, of the bytecode.</summary>
    public nuint BufferLength => ((ID3DBlob*)m_blobHandle)->GetBufferSize();
    /// <summary>Gets a pointer to the bytecode bytes.</summary>
    public nint BufferPointer => (nint)((ID3DBlob*)m_blobHandle)->GetBufferPointer();

    /// <summary>Releases the owned blob. Safe to call more than once.</summary>
    public void Dispose() {
        var handle = Interlocked.Exchange(
            location1: ref m_blobHandle,
            value: 0
        );

        if (0 != handle) {
            _ = ((ID3DBlob*)handle)->Release();
        }
    }
}
