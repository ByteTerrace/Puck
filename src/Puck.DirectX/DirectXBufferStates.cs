using System.Runtime.Versioning;
using Windows.Win32.Graphics.Direct3D12;

namespace Puck.DirectX;

/// <summary>Names the barrier a buffer transition records.</summary>
public enum DirectXBufferBarrierKind {
    /// <summary>The buffer already holds a state covering the requested access; nothing is recorded.</summary>
    None,
    /// <summary>The buffer stays in <c>UNORDERED_ACCESS</c>; a UAV barrier orders the earlier writes before the next access.</summary>
    UnorderedAccess,
    /// <summary>A transition barrier moves the buffer from <see cref="DirectXBufferBarrier.Before"/> to <see cref="DirectXBufferBarrier.After"/>.</summary>
    Transition,
}
/// <summary>The barrier planned for one buffer transition and the states it moves between.</summary>
/// <param name="Kind">The barrier to record.</param>
/// <param name="Before">The state the buffer holds before the barrier.</param>
/// <param name="After">The state the requested access needs.</param>
public readonly record struct DirectXBufferBarrier(DirectXBufferBarrierKind Kind, D3D12_RESOURCE_STATES Before, D3D12_RESOURCE_STATES After);
/// <summary>
/// Tracks the states that explicit transitions leave buffers in within one command list and plans each buffer barrier
/// from them. Direct3D 12 decays every buffer to <c>COMMON</c> when the <c>ExecuteCommandLists</c> that used it
/// completes, and a later access promotes it implicitly, so a recorded state holds only until the command list begins
/// its next recording and <see cref="Reset"/> clears it.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed class DirectXBufferStates {
    private readonly Dictionary<nint, D3D12_RESOURCE_STATES> m_states = [];

    /// <summary>Returns the state a buffer access needs.</summary>
    /// <param name="access">The neutral access. A write through a read-write binding, a read through one (declared with
    /// the write the binding permits), and a transfer write, which Direct3D 12 performs as a UAV clear, all need
    /// <c>UNORDERED_ACCESS</c>.</param>
    /// <param name="stages">The stages that make the access. A shader read by the fragment stage needs the pixel shader
    /// resource state as well as the non-pixel one, since the one state covers every shader stage that may read the
    /// buffer.</param>
    /// <returns><c>UNORDERED_ACCESS</c> for a transfer write, then <c>INDIRECT_ARGUMENT</c> for an indirect-argument
    /// read, then <c>UNORDERED_ACCESS</c> for a shader write, then for a shader read <c>ALL_SHADER_RESOURCE</c> when
    /// <paramref name="stages"/> holds the fragment stage and <c>NON_PIXEL_SHADER_RESOURCE</c> otherwise, then
    /// <c>COMMON</c>; the first match wins.</returns>
    public static D3D12_RESOURCE_STATES RequiredState(GpuAccess access, GpuStage stages) {
        if (0 != (access & GpuAccess.TransferWrite)) {
            return D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_UNORDERED_ACCESS;
        }

        if (0 != (access & GpuAccess.IndirectCommandRead)) {
            return D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_INDIRECT_ARGUMENT;
        }

        if (0 != (access & GpuAccess.ShaderWrite)) {
            return D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_UNORDERED_ACCESS;
        }

        if (0 != (access & GpuAccess.ShaderRead)) {
            return ((0 != (stages & GpuStage.FragmentShader))
                ? D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_ALL_SHADER_RESOURCE
                : D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE
            );
        }

        return D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON;
    }
    /// <summary>Forgets every recorded state, as the command list begins a new recording.</summary>
    public void Reset() => m_states.Clear();
    /// <summary>Plans the barrier that readies a buffer for <paramref name="after"/> and records the state a transition leaves it in.</summary>
    /// <param name="bufferHandle">The native <c>ID3D12Resource*</c> of the buffer.</param>
    /// <param name="firstState">The state the buffer holds before its first transition in this recording: the state its declared prior access promoted it to, or an upload-heap buffer's permanent <c>GENERIC_READ</c>.</param>
    /// <param name="after">The state the next access needs.</param>
    /// <returns>The barrier to record: none when the held state already covers a read state, a UAV barrier when the buffer stays in <c>UNORDERED_ACCESS</c>, otherwise a transition.</returns>
    public DirectXBufferBarrier Plan(nint bufferHandle, D3D12_RESOURCE_STATES firstState, D3D12_RESOURCE_STATES after) {
        var before = (m_states.TryGetValue(
            key: bufferHandle,
            value: out var recorded
        )
            ? recorded
            : firstState);

        if (
            (before == after) ||
            ((after != D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_UNORDERED_ACCESS) && ((before & after) == after))
        ) {
            return new DirectXBufferBarrier(
                After: after,
                Before: before,
                Kind: (((after & D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_UNORDERED_ACCESS) != 0)
                    ? DirectXBufferBarrierKind.UnorderedAccess
                    : DirectXBufferBarrierKind.None)
            );
        }

        m_states[bufferHandle] = after;

        return new DirectXBufferBarrier(
            After: after,
            Before: before,
            Kind: DirectXBufferBarrierKind.Transition
        );
    }
}
