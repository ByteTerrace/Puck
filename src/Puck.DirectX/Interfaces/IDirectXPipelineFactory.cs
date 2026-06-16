using System.Runtime.Versioning;
using Puck.DirectX.Interop;

namespace Puck.DirectX.Interfaces;

/// <summary>
/// Creates an owning <see cref="DirectXPipeline"/> from compiled shader bytecode, against a device context.
/// The peer of <c>IVulkanGraphicsPipelineFactory</c>.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public interface IDirectXPipelineFactory {
    /// <summary>Creates a flat-shaded (<c>POSITION</c> + <c>COLOR</c>) graphics pipeline over the given vertex and pixel shaders.</summary>
    /// <param name="deviceContext">The device the pipeline is created on.</param>
    /// <param name="vertexShader">The compiled vertex shader bytecode.</param>
    /// <param name="pixelShader">The compiled pixel shader bytecode.</param>
    /// <returns>A new, owning <see cref="DirectXPipeline"/>.</returns>
    /// <exception cref="DirectXException">A Direct3D 12 call failed.</exception>
    DirectXPipeline Create(
        IDirectXDeviceContext deviceContext,
        DirectXShaderBytecode vertexShader,
        DirectXShaderBytecode pixelShader
    );
    /// <summary>Creates a textured (<c>POSITION</c> + <c>TEXCOORD</c>) graphics pipeline — with an SRV descriptor table and static sampler — over the given vertex and pixel shaders.</summary>
    /// <param name="deviceContext">The device the pipeline is created on.</param>
    /// <param name="vertexShader">The compiled vertex shader bytecode.</param>
    /// <param name="pixelShader">The compiled pixel shader bytecode.</param>
    /// <returns>A new, owning <see cref="DirectXPipeline"/>.</returns>
    /// <exception cref="DirectXException">A Direct3D 12 call failed.</exception>
    DirectXPipeline CreateTextured(
        IDirectXDeviceContext deviceContext,
        DirectXShaderBytecode vertexShader,
        DirectXShaderBytecode pixelShader
    );
}
