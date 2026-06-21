using System.Runtime.Versioning;
using Puck.DirectX.Interop;

namespace Puck.DirectX;

/// <summary>
/// Implements <see cref="IGpuShaderModuleFactory"/> for Direct3D 12 by pinning the supplied DXIL bytecode
/// in managed memory so it is directly addressable as a <c>D3D12_SHADER_BYTECODE</c>.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed class DirectXGpuShaderModuleFactory : IGpuShaderModuleFactory {
    /// <inheritdoc/>
    public IGpuShaderModule Create(IGpuDeviceContext deviceContext, uint stage, ReadOnlyMemory<byte> bytecode) {
        ShaderBytecode.ValidateFormat(bytecode: bytecode.Span);

        return new DirectXGpuShaderModule(bytecode: bytecode);
    }
}
