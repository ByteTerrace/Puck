using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32.Graphics.Direct3D12;

namespace Puck.DirectX;

// The recorder's device work: the counted indirect draw, which a command list on a live device executes.
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe partial class DirectXGpuRecorder {
    /// <inheritdoc/>
    public bool CanDrawIndirectCount => true;

    /// <inheritdoc/>
    /// <remarks>Each pipeline layout's command signature (<see cref="DirectXPipelineLayout.DrawSignature"/>) writes a
    /// record's identity into the layout's root constants, then draws; a layout without root constants skips the
    /// identity. No transition is recorded here: both buffers reach <c>INDIRECT_ARGUMENT</c> through the caller's
    /// <see cref="TransitionBuffer"/>.</remarks>
    public void DrawIndirectCount(nint commandBufferHandle, nint pipelineLayoutHandle, nint argumentBufferHandle, ulong argumentBufferOffset, nint countBufferHandle, ulong countBufferOffset, uint maxDrawCount) {
        var state = DecodeState(commandBufferHandle: commandBufferHandle);
        var layout = ((DirectXPipelineLayout)GCHandle.FromIntPtr(value: pipelineLayoutHandle).Target!);
        var signature = layout.DrawSignature(device: ((ID3D12Device*)deviceContext.Device.Handle));

        ((ID3D12GraphicsCommandList*)state.CommandList)->ExecuteIndirect(
            ArgumentBufferOffset: (argumentBufferOffset + ((0 > layout.RootConstantsParamIndex) ? (ulong)(GpuDrawIndirectCommand.VertexCountWord * sizeof(uint)) : 0UL)),
            CountBufferOffset: countBufferOffset,
            MaxCommandCount: maxDrawCount,
            pArgumentBuffer: ((ID3D12Resource*)argumentBufferHandle),
            pCommandSignature: ((ID3D12CommandSignature*)signature),
            pCountBuffer: ((ID3D12Resource*)countBufferHandle)
        );
    }
}
