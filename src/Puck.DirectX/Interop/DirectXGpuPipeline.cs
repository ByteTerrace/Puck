using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Puck.DirectX.Interop;

/// <summary>
/// A Direct3D 12 graphics or compute pipeline. All three handle properties
/// (<see cref="Handle"/>, <see cref="LayoutHandle"/>, <see cref="DescriptorSetLayoutHandle"/>) return the
/// same GCHandle token pointing to a <see cref="DirectXPipelineLayout"/>, which carries the PSO, root
/// signature, and parameter indices the command recorder needs.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed class DirectXGpuPipeline : IGpuPipeline, IGpuComputePipeline {
    private readonly GCHandle m_token;

    private bool m_disposed;

    /// <summary>Initializes a new instance wrapping the given layout.</summary>
    public DirectXGpuPipeline(DirectXPipelineLayout layout) {
        ArgumentNullException.ThrowIfNull(layout);

        m_token = GCHandle.Alloc(value: layout);
    }

    /// <inheritdoc/>
    public nint DescriptorSetLayoutHandle => GCHandle.ToIntPtr(value: m_token);
    /// <inheritdoc/>
    public nint Handle => GCHandle.ToIntPtr(value: m_token);
    /// <inheritdoc/>
    public nint LayoutHandle => GCHandle.ToIntPtr(value: m_token);

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;

        var layout = ((DirectXPipelineLayout)m_token.Target!);

        layout.Dispose();
        m_token.Free();
    }
}
