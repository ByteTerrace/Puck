using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;

namespace Puck.Vulkan;

/// <summary>
/// Implements <see cref="IGpuPipelineFactory"/> by forwarding to <see cref="IVulkanGraphicsPipelineFactory"/>,
/// downcasting the device context, render target, and shader modules to their Vulkan-specific types.
/// </summary>
public sealed class VulkanGpuPipelineFactory(IVulkanGraphicsPipelineFactory pipelineFactory) : IGpuPipelineFactory {
    /// <inheritdoc/>
    public IGpuPipeline Create(
        IGpuDeviceContext deviceContext,
        IGpuRenderTarget renderTarget,
        IGpuShaderModule vertexShaderModule,
        IGpuShaderModule fragmentShaderModule,
        GpuGraphicsPipelineDescription description,
        uint width,
        uint height
    ) {
        var logicalDevice = ((IVulkanDeviceContext)deviceContext).LogicalDevice;
        var renderPass = ((IVulkanRenderTarget)renderTarget).RenderPass;
        var vertexShader = ((VulkanShaderModule)vertexShaderModule);
        var fragmentShader = ((VulkanShaderModule)fragmentShaderModule);
        var pushConstantBinding = description.PushConstantBinding;
        var vkPushConstant = ((pushConstantBinding is null) ? null
            : new VulkanPushConstantBinding(
                data: pushConstantBinding.Data,
                offset: pushConstantBinding.Offset,
                stageFlags: ((uint)pushConstantBinding.StageFlags)
            ));

        // The vertex input layout is not forwarded: IVulkanGraphicsPipelineFactory hardcodes the same fixed
        // POSITION-only shape one layer down (the counterpart to Direct3D's now-data-driven input layout), and
        // every caller of this factory authors that exact shape today — describing it as data here does not open
        // a door Vulkan silently ignores, since no caller varies it.
        return pipelineFactory.Create(
            enableStorageBuffer: description.EnableStorageBuffer,
            fragmentShaderModule: fragmentShader,
            height: height,
            logicalDevice: logicalDevice,
            pushConstantBinding: vkPushConstant,
            renderPass: renderPass,
            textureSamplerCount: description.TextureSamplerCount,
            vertexShaderModule: vertexShader,
            width: width
        );
    }
}
