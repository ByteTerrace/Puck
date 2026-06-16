using System.Runtime.Versioning;
using Windows.Win32.System.Com;

namespace Puck.DirectX.Interop;

/// <summary>
/// Owns a graphics pipeline's root signature and pipeline state object, releasing both on disposal. The
/// Direct3D 12 analog of a <c>VulkanGraphicsPipeline</c> (which owns its pipeline and layout).
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXPipeline : IDisposable {
    private nint m_pipelineState;
    private nint m_rootSignature;

    /// <summary>Initializes a new instance of the <see cref="DirectXPipeline"/> class taking ownership of a root signature and pipeline state.</summary>
    /// <param name="rootSignatureHandle">The native <c>ID3D12RootSignature</c> pointer to own.</param>
    /// <param name="pipelineStateHandle">The native <c>ID3D12PipelineState</c> pointer to own.</param>
    /// <exception cref="ArgumentException">A handle is zero.</exception>
    public DirectXPipeline(nint rootSignatureHandle, nint pipelineStateHandle) {
        if (0 == rootSignatureHandle) {
            throw new ArgumentException(
                message: "Root signature handle must be non-zero.",
                paramName: nameof(rootSignatureHandle)
            );
        }

        if (0 == pipelineStateHandle) {
            throw new ArgumentException(
                message: "Pipeline state handle must be non-zero.",
                paramName: nameof(pipelineStateHandle)
            );
        }

        m_pipelineState = pipelineStateHandle;
        m_rootSignature = rootSignatureHandle;
    }

    /// <summary>Gets the native <c>ID3D12PipelineState</c> handle, or zero once disposed.</summary>
    public nint PipelineStateHandle => m_pipelineState;
    /// <summary>Gets the native <c>ID3D12RootSignature</c> handle, or zero once disposed.</summary>
    public nint RootSignatureHandle => m_rootSignature;

    /// <summary>Releases the pipeline state and root signature. Safe to call more than once.</summary>
    public void Dispose() {
        var pipelineState = Interlocked.Exchange(
            location1: ref m_pipelineState,
            value: 0
        );

        if (0 != pipelineState) {
            _ = ((IUnknown*)pipelineState)->Release();
        }

        var rootSignature = Interlocked.Exchange(
            location1: ref m_rootSignature,
            value: 0
        );

        if (0 != rootSignature) {
            _ = ((IUnknown*)rootSignature)->Release();
        }
    }
}
