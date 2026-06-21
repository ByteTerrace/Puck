using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32.System.Com;

namespace Puck.DirectX.Interop;

/// <summary>
/// A Direct3D 12 compute pipeline implementing <see cref="IGpuComputePipeline"/>. All three handle properties
/// return the same GCHandle token pointing to a <see cref="DirectXPipelineLayout"/> (the PSO, root signature, and
/// parameter indices the compute recorder binds through) — there is no separate descriptor-set-layout object in
/// Direct3D 12.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXGpuComputePipeline : IGpuComputePipeline {
    private readonly GCHandle m_token;
    private bool m_disposed;

    /// <summary>Initializes a new instance wrapping the given layout.</summary>
    public DirectXGpuComputePipeline(DirectXPipelineLayout layout) {
        ArgumentNullException.ThrowIfNull(layout);

        m_token = GCHandle.Alloc(layout);
    }

    /// <inheritdoc/>
    public nint DescriptorSetLayoutHandle => GCHandle.ToIntPtr(m_token);
    /// <inheritdoc/>
    public nint Handle => GCHandle.ToIntPtr(m_token);
    /// <inheritdoc/>
    public nint LayoutHandle => GCHandle.ToIntPtr(m_token);

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;

        var layout = (DirectXPipelineLayout)m_token.Target!;

        if (0 != layout.PsoHandle) {
            _ = ((IUnknown*)layout.PsoHandle)->Release();
            layout.PsoHandle = 0;
        }

        if (0 != layout.RootSignatureHandle) {
            _ = ((IUnknown*)layout.RootSignatureHandle)->Release();
            layout.RootSignatureHandle = 0;
        }

        m_token.Free();
    }
}
