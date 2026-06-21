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
        GpuPushConstantBinding? pushConstantBinding,
        uint textureSamplerCount,
        bool enableStorageBuffer,
        uint width,
        uint height
    ) {
        var logicalDevice = ((IVulkanDeviceContext)deviceContext).LogicalDevice;
        var renderPass = ((IVulkanRenderTarget)renderTarget).RenderPass;
        var vertexShader = (VulkanShaderModule)vertexShaderModule;
        var fragmentShader = (VulkanShaderModule)fragmentShaderModule;
        var vkPushConstant = pushConstantBinding is null ? null
            : new VulkanPushConstantBinding(
                data: pushConstantBinding.Data,
                offset: pushConstantBinding.Offset,
                stageFlags: pushConstantBinding.StageFlags
            );

        return pipelineFactory.Create(
            enableStorageBuffer: enableStorageBuffer,
            fragmentShaderModule: fragmentShader,
            height: height,
            logicalDevice: logicalDevice,
            pushConstantBinding: vkPushConstant,
            renderPass: renderPass,
            textureSamplerCount: textureSamplerCount,
            vertexShaderModule: vertexShader,
            width: width
        );
    }
}
