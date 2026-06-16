using System.Runtime.Versioning;
using Puck.DirectX.Interop;
using Puck.DirectX.Messages;

namespace Puck.DirectX.Interfaces;

/// <summary>
/// Compiles HLSL source to a shader bytecode blob. The Direct3D 12 peer of the Vulkan shader-module API —
/// where Vulkan loads precompiled SPIR-V, this compiles HLSL on demand to the DXBC a pipeline embeds.
/// </summary>
[SupportedOSPlatform("windows8.1")]
public interface IDirectXShaderCompilerApi {
    /// <summary>Compiles an HLSL shader to bytecode.</summary>
    /// <param name="request">The shader source, entry point, and target profile.</param>
    /// <returns>An owning <see cref="DirectXShaderBytecode"/>.</returns>
    /// <exception cref="DirectXException">Compilation failed; the message includes the compiler diagnostics.</exception>
    DirectXShaderBytecode Compile(DirectXShaderCompileRequest request);
}
