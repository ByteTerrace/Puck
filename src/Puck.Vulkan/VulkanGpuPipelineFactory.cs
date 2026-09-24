using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;

namespace Puck.Vulkan;

/// <summary>
/// Implements <see cref="IGpuPipelineFactory"/> by forwarding to <see cref="IVulkanGraphicsPipelineFactory"/>,
/// downcasting the device context, render pass, and shader modules to their Vulkan-specific types. The pipeline writes
/// every color attachment of the render pass opaquely, tests and writes its depth attachment by the description's
/// comparison, and points clip-space +y at the top of the attachment, as Direct3D 12 does.
/// </summary>
public sealed class VulkanGpuPipelineFactory(IVulkanGraphicsPipelineFactory pipelineFactory) : IGpuPipelineFactory {
    /// <summary>Converts a depth comparison to its <c>VkCompareOp</c>.</summary>
    /// <param name="compare">The comparison.</param>
    /// <returns>The <c>VkCompareOp</c> value.</returns>
    public static uint ToVkCompareOp(GpuDepthCompare compare) => compare switch {
        GpuDepthCompare.Less => 1U,
        GpuDepthCompare.Equal => 2U,
        GpuDepthCompare.LessOrEqual => 3U,
        GpuDepthCompare.Greater => 4U,
        GpuDepthCompare.GreaterOrEqual => 6U,
        GpuDepthCompare.Always => 7U,
        _ => throw new ArgumentOutOfRangeException(
            actualValue: compare,
            message: "The depth comparison is not defined.",
            paramName: nameof(compare)
        ),
    };
    /// <inheritdoc/>
    public IGpuPipeline Create(
        IGpuDeviceContext deviceContext,
        IGpuRenderPass renderPass,
        IGpuShaderModule vertexShaderModule,
        IGpuShaderModule fragmentShaderModule,
        GpuGraphicsPipelineDescription description,
        uint width,
        uint height
    ) {
        ArgumentNullException.ThrowIfNull(description);
        description.ValidateAgainst(renderPass: renderPass);

        var logicalDevice = ((IVulkanDeviceContext)deviceContext).LogicalDevice;
        var pass = ((VulkanGpuRenderPass)renderPass);
        var vertexShader = ((VulkanShaderModule)vertexShaderModule);
        var fragmentShader = ((VulkanShaderModule)fragmentShaderModule);
        var pushConstantBinding = description.PushConstantBinding;
        var vkPushConstant = ((pushConstantBinding is null)
            ? null
            : new VulkanPushConstantBinding(
                data: pushConstantBinding.Data,
                offset: pushConstantBinding.Offset,
                stageFlags: ((uint)pushConstantBinding.StageFlags)
            )
        );

        return pipelineFactory.Create(
            enableStorageBuffer: description.EnableStorageBuffer,
            fragmentShaderModule: fragmentShader,
            height: height,
            logicalDevice: logicalDevice,
            outputs: new VulkanGraphicsOutputs(
                AlphaBlend: false,
                ClipSpaceYUp: true,
                ColorAttachmentCount: ((uint)pass.Description.Colors.Count),
                DepthCompareOp: ((description.DepthCompare is { } compare)
                    ? ToVkCompareOp(compare: compare)
                    : null)
            ),
            pushConstantBinding: vkPushConstant,
            renderPass: pass.RenderPass,
            textureSamplerCount: description.TextureSamplerCount,
            vertexInput: description.VertexInput,
            vertexShaderModule: vertexShader,
            width: width
        );
    }
}
