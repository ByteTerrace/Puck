using System.Runtime.Versioning;
using Windows.Win32.Graphics.Direct3D12;

namespace Puck.DirectX;

// The layout's device work: the command signature a counted indirect draw needs, created on a live device.
[SupportedOSPlatform("windows10.0.10240")]
public sealed partial class DirectXPipelineLayout {
    /// <summary>Returns the command signature a counted indirect draw through this layout executes, creating it on first
    /// use: a root-constant argument writing a record's identity into the layout's root constants, then a draw, over
    /// <see cref="GpuDrawIndirectCommand.StrideBytes"/>-byte records; a layout without root constants draws alone. It is
    /// released with the layout.</summary>
    /// <param name="device">The device the layout's root signature was created on.</param>
    /// <returns>The native <c>ID3D12CommandSignature*</c>.</returns>
    internal unsafe nint DrawSignature(ID3D12Device* device) {
        lock (m_drawSignatureLock) {
            if (0 != m_drawSignature) {
                return m_drawSignature;
            }

            var arguments = stackalloc D3D12_INDIRECT_ARGUMENT_DESC[2];
            var count = 0u;

            if (0 <= RootConstantsParamIndex) {
                arguments[count].Type = D3D12_INDIRECT_ARGUMENT_TYPE.D3D12_INDIRECT_ARGUMENT_TYPE_CONSTANT;
                arguments[count].Anonymous.Constant.RootParameterIndex = ((uint)RootConstantsParamIndex);
                arguments[count].Anonymous.Constant.DestOffsetIn32BitValues = 0;
                arguments[count].Anonymous.Constant.Num32BitValuesToSet = 1;
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

            return m_drawSignature;
        }
    }
}
