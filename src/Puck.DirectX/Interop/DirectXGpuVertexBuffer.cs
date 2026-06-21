using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Puck.DirectX.Interop;

/// <summary>
/// Wraps a <see cref="DirectXVertexBuffer"/> as an <see cref="IGpuVertexBuffer"/>. <see cref="BufferHandle"/>
/// is a GCHandle token pointing to a <see cref="DirectXVertexBufferView"/> so the command recorder can
/// reconstruct the full <c>D3D12_VERTEX_BUFFER_VIEW</c> from a single nint.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed class DirectXGpuVertexBuffer : IGpuVertexBuffer {
    private readonly DirectXVertexBuffer m_inner;
    private readonly GCHandle m_token;

    /// <summary>Initializes a new instance wrapping the given vertex buffer.</summary>
    public DirectXGpuVertexBuffer(DirectXVertexBuffer inner) {
        ArgumentNullException.ThrowIfNull(inner);

        m_inner = inner;

        var view = new DirectXVertexBufferView {
            BufferLocation = inner.BufferLocation,
            SizeBytes = inner.SizeBytes,
            StrideBytes = inner.StrideBytes,
        };

        m_token = GCHandle.Alloc(view);
    }

    /// <inheritdoc/>
    public nint BufferHandle => GCHandle.ToIntPtr(m_token);

    /// <inheritdoc/>
    public void Dispose() {
        if (m_token.IsAllocated) {
            m_token.Free();
        }

        m_inner.Dispose();
    }
}
