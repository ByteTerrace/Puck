using Puck.Assets;
using Puck.Vulkan;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Launcher.Vulkan;

/// <summary>Records a single swapchain image's command buffer for the immediate-mode compositor: one
/// render pass replaying caller-supplied draw commands (bind pipeline, vertex buffer, push constants,
/// descriptor set, draw). Scissor is the full framebuffer; viewport is baked into the pipeline.</summary>
public sealed class VulkanCommandBufferRecorder : IVulkanCommandBufferRecorder {
    private readonly IVulkanCommandBufferRecordingApi m_commandBufferRecordingApi;

    public VulkanCommandBufferRecorder(IVulkanCommandBufferRecordingApi commandBufferRecordingApi) {
        ArgumentNullException.ThrowIfNull(commandBufferRecordingApi);

        m_commandBufferRecordingApi = commandBufferRecordingApi;
    }

    private static VulkanGraphicsPipeline SelectPipeline(
        IReadOnlyDictionary<AssetContentHash, VulkanGraphicsPipeline> graphicsPipelines,
        AssetContentHash pipelineId
    ) {
        if (graphicsPipelines.TryGetValue(
            key: pipelineId,
            value: out var pipeline
        )) {
            return pipeline;
        }

        throw new InvalidOperationException(message: $"No configured graphics pipeline found for pipeline id '{pipelineId}'.");
    }

    public void RecordImage(
        VulkanCommandResources commandResources,
        int imageIndex,
        VulkanFramebufferSet framebufferSet,
        VulkanRenderPass renderPass,
        IReadOnlyDictionary<AssetContentHash, VulkanGraphicsPipeline> graphicsPipelines,
        VulkanSwapchain swapchain,
        IReadOnlyList<VulkanDrawCommand> drawCommands
    ) {
        ArgumentNullException.ThrowIfNull(commandResources);
        ArgumentNullException.ThrowIfNull(framebufferSet);
        ArgumentNullException.ThrowIfNull(renderPass);
        ArgumentNullException.ThrowIfNull(graphicsPipelines);
        ArgumentNullException.ThrowIfNull(swapchain);
        ArgumentNullException.ThrowIfNull(drawCommands);

        if (graphicsPipelines.Count == 0) {
            throw new InvalidOperationException(message: "Command-buffer recording requires at least one configured graphics pipeline.");
        }

        if (drawCommands.Count == 0) {
            throw new InvalidOperationException(message: "Command-buffer recording requires at least one draw command.");
        }

        if (commandResources.CommandBufferHandles.Count != framebufferSet.FramebufferHandles.Count) {
            throw new InvalidOperationException(message: "Command-buffer recording requires one command buffer for each framebuffer.");
        }

        if (
            (imageIndex < 0) ||
            (imageIndex >= commandResources.CommandBufferHandles.Count)
        ) {
            throw new ArgumentOutOfRangeException(
                actualValue: imageIndex,
                message: "Command-buffer recording requires an image index that maps to an allocated command buffer.",
                paramName: nameof(imageIndex)
            );
        }

        var primaryPipeline = graphicsPipelines.First();
        var request = new VulkanCommandBufferRecordRequest(
            CommandBufferHandle: commandResources.CommandBufferHandles[imageIndex],
            DeviceHandle: commandResources.DeviceHandle,
            FramebufferHandle: framebufferSet.FramebufferHandles[imageIndex],
            GraphicsPipelineHandle: primaryPipeline.Value.Handle,
            Height: swapchain.ImageExtentHeight,
            RenderPassHandle: renderPass.Handle,
            Width: swapchain.ImageExtentWidth
        );

        // Begin implicitly resets this one buffer (pool created with RESET_COMMAND_BUFFER_BIT); the
        // caller's in-flight fence wait guarantees the buffer is not pending.
        m_commandBufferRecordingApi.BeginCommandBuffer(request: request).ThrowIfFailed(operation: "vkBeginCommandBuffer");
        m_commandBufferRecordingApi.StartRenderPass(request: request);
        // Dynamic scissor over the whole framebuffer; the pipeline bakes the matching viewport.
        m_commandBufferRecordingApi.SetScissor(
            commandBufferHandle: request.CommandBufferHandle,
            deviceHandle: request.DeviceHandle,
            height: request.Height,
            width: request.Width,
            x: 0,
            y: 0
        );

        nint currentPipelineHandle = 0;
        nint currentPipelineLayoutHandle = 0;

        foreach (var drawCommand in drawCommands) {
            var pipelineId = ((drawCommand.PipelineId == default)
                ? primaryPipeline.Key
                : drawCommand.PipelineId);
            var selectedPipeline = SelectPipeline(
                graphicsPipelines: graphicsPipelines,
                pipelineId: pipelineId
            );

            if (selectedPipeline.Handle != currentPipelineHandle) {
                m_commandBufferRecordingApi.BindGraphicsPipeline(
                    commandBufferHandle: request.CommandBufferHandle,
                    deviceHandle: request.DeviceHandle,
                    pipelineHandle: selectedPipeline.Handle
                );
                currentPipelineHandle = selectedPipeline.Handle;
                currentPipelineLayoutHandle = selectedPipeline.LayoutHandle;
            }

            if (drawCommand.VertexBufferBinding is VulkanVertexBufferBinding vertexBufferBinding) {
                m_commandBufferRecordingApi.BindVertexBuffer(
                    commandBufferHandle: request.CommandBufferHandle,
                    deviceHandle: request.DeviceHandle,
                    vertexBufferBinding: vertexBufferBinding
                );
            }

            if (drawCommand.PushConstantBinding is VulkanPushConstantBinding pushConstantBinding) {
                m_commandBufferRecordingApi.PushConstants(
                    commandBufferHandle: request.CommandBufferHandle,
                    data: pushConstantBinding.Data.Span,
                    deviceHandle: request.DeviceHandle,
                    offset: pushConstantBinding.Offset,
                    pipelineLayoutHandle: currentPipelineLayoutHandle,
                    stageFlags: pushConstantBinding.StageFlags
                );
            }

            if (drawCommand.DescriptorSetHandle != 0) {
                m_commandBufferRecordingApi.BindDescriptorSet(
                    commandBufferHandle: request.CommandBufferHandle,
                    descriptorSetHandle: drawCommand.DescriptorSetHandle,
                    deviceHandle: request.DeviceHandle,
                    pipelineLayoutHandle: currentPipelineLayoutHandle
                );
            }

            m_commandBufferRecordingApi.Draw(
                commandBufferHandle: request.CommandBufferHandle,
                deviceHandle: request.DeviceHandle,
                firstInstance: drawCommand.DrawParameters.FirstInstance,
                firstVertex: drawCommand.DrawParameters.FirstVertex,
                instanceCount: drawCommand.DrawParameters.InstanceCount,
                vertexCount: drawCommand.DrawParameters.VertexCount
            );
        }

        m_commandBufferRecordingApi.EndRenderPass(
            commandBufferHandle: request.CommandBufferHandle,
            deviceHandle: request.DeviceHandle
        );
        m_commandBufferRecordingApi.EndCommandBuffer(
            commandBufferHandle: request.CommandBufferHandle,
            deviceHandle: request.DeviceHandle
        ).ThrowIfFailed(operation: "vkEndCommandBuffer");
    }
}
