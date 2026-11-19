using System.Runtime.Versioning;
using Windows.Win32.Graphics.Direct3D12;

namespace Puck.DirectX;

// The layout's device work: the command signature a counted indirect draw needs, created on a live device.
[SupportedOSPlatform("windows10.0.10240")]
public sealed partial class DirectXPipelineLayout {
    /// <summary>Returns the command signature a counted indirect draw through this layout executes, creating it on first
    /// use. A layout with root constants numbers the draws: with <paramref name="numbered"/> the command processor writes
    /// each command's position into the layout's root constants as an incrementing constant, which takes no argument
    /// space, and a command is its draw alone; without it a root-constant argument copies a record's identity word into
    /// them ahead of its draw. A layout without root constants draws alone. Commands are
    /// <see cref="GpuDrawIndirectCommand.StrideBytes"/> bytes apart. It is released with the layout.</summary>
    /// <param name="device">The device the layout's root signature was created on.</param>
    /// <param name="numbered">Whether the device's execute-indirect tier admits an incrementing constant
    /// (<see cref="GpuDeviceCapabilities.ExecuteIndirectIncrementingConstant"/>).</param>
    /// <returns>The native <c>ID3D12CommandSignature*</c>.</returns>
    internal unsafe nint DrawSignature(ID3D12Device* device, bool numbered) {
        lock (m_drawSignatureLock) {
            if (0 != m_drawSignature) {
                return m_drawSignature;
            }

            var arguments = stackalloc D3D12_INDIRECT_ARGUMENT_DESC[2];
            var count = 0u;

            if (0 <= RootConstantsParamIndex) {
                if (numbered) {
                    arguments[count].Type = D3D12_INDIRECT_ARGUMENT_TYPE.D3D12_INDIRECT_ARGUMENT_TYPE_INCREMENTING_CONSTANT;
                    arguments[count].Anonymous.IncrementingConstant.RootParameterIndex = ((uint)RootConstantsParamIndex);
                    arguments[count].Anonymous.IncrementingConstant.DestOffsetIn32BitValues = 0;
                } else {
                    arguments[count].Type = D3D12_INDIRECT_ARGUMENT_TYPE.D3D12_INDIRECT_ARGUMENT_TYPE_CONSTANT;
                    arguments[count].Anonymous.Constant.RootParameterIndex = ((uint)RootConstantsParamIndex);
                    arguments[count].Anonymous.Constant.DestOffsetIn32BitValues = 0;
                    arguments[count].Anonymous.Constant.Num32BitValuesToSet = 1;
                }

                count++;
            }

            arguments[count].Type = D3D12_INDIRECT_ARGUMENT_TYPE.D3D12_INDIRECT_ARGUMENT_TYPE_DRAW;
            count++;

            var description = new D3D12_COMMAND_SIGNATURE_DESC {
                ByteStride = GpuDrawIndirectCommand.StrideBytes,
                NumArgumentDescs = count,
                pArgumentDescs = arguments,
            };

            device->CreateCommandSignature(
                pDesc: in description,
                pRootSignature: ((0 <= RootConstantsParamIndex) ? ((ID3D12RootSignature*)RootSignatureHandle) : null),
                ppvCommandSignature: out ID3D12CommandSignature* signature
            );
            m_drawSignature = ((nint)signature);
            m_drawSkipsIdentity = (numbered || (0 > RootConstantsParamIndex));

            return m_drawSignature;
        }
    }
    /// <summary>Gets whether a command of the signature <see cref="DrawSignature"/> created starts at its draw arguments, one
    /// word past its identity, rather than at the identity word a root-constant argument reads.</summary>
    internal bool DrawSkipsIdentity => m_drawSkipsIdentity;
}
