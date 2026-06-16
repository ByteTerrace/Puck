using System.Runtime.Versioning;
using Puck.DirectX.Messages;

namespace Puck.DirectX.Interfaces;

/// <summary>
/// Wraps the Direct3D 12 root-signature and graphics-pipeline-state creation entry points. The peer of the
/// Vulkan graphics-pipeline API: like it, the input layout, render-target format, and fixed-function state are
/// baked in here so callers supply only the device and the shader bytecode.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public interface IDirectXPipelineApi {
    /// <summary>Creates an empty root signature and a flat-shaded (<c>POSITION</c> + <c>COLOR</c>) graphics pipeline over the given shaders.</summary>
    /// <param name="request">The device and shader bytecode the pipeline is built from.</param>
    /// <returns>The created root-signature and pipeline-state handles.</returns>
    /// <exception cref="DirectXException">A Direct3D 12 call failed.</exception>
    DirectXGraphicsPipelineCreateResult CreateGraphicsPipeline(DirectXGraphicsPipelineCreateRequest request);
    /// <summary>Creates a root signature with a single SRV descriptor table and static sampler, and a textured (<c>POSITION</c> + <c>TEXCOORD</c>) graphics pipeline over the given shaders.</summary>
    /// <param name="request">The device and shader bytecode the pipeline is built from.</param>
    /// <returns>The created root-signature and pipeline-state handles.</returns>
    /// <exception cref="DirectXException">A Direct3D 12 call failed.</exception>
    DirectXGraphicsPipelineCreateResult CreateTexturedGraphicsPipeline(DirectXGraphicsPipelineCreateRequest request);
}
