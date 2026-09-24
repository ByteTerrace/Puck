using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan;

/// <summary>
/// Implements <see cref="IGpuCommandRecorder"/> by forwarding to <see cref="IVulkanCommandBufferRecordingApi"/>.
/// </summary>
public sealed class VulkanGpuCommandRecorder(IVulkanCommandBufferRecordingApi commandBufferRecordingApi) : IGpuCommandRecorder {
    /// <inheritdoc/>
    public void BeginCommandBuffer(nint deviceHandle, nint commandBufferHandle) {
        commandBufferRecordingApi.BeginCommandBuffer(
            commandBufferHandle: commandBufferHandle,
            device: VulkanDeviceCommands.FromToken(token: deviceHandle)
        ).ThrowIfFailed(operation: "vkBeginCommandBuffer");
    }
    /// <inheritdoc/>
    public void BeginDebugGroup(nint deviceHandle, nint commandBufferHandle, string label) {
        commandBufferRecordingApi.BeginDebugLabel(
            commandBufferHandle: commandBufferHandle,
            device: VulkanDeviceCommands.FromToken(token: deviceHandle),
            label: label
        );
    }
    /// <inheritdoc/>
    public void BeginRenderPass(nint deviceHandle, nint commandBufferHandle, IGpuFramebuffer framebuffer) {
        var vulkanFramebuffer = ((VulkanGpuFramebuffer)framebuffer);

        commandBufferRecordingApi.StartRenderPass(request: new VulkanCommandBufferRecordRequest(
            ClearValues: vulkanFramebuffer.Pass.ClearValues,
            CommandBufferHandle: commandBufferHandle,
            Device: VulkanDeviceCommands.FromToken(token: deviceHandle),
            FramebufferHandle: vulkanFramebuffer.Handle,
            Height: vulkanFramebuffer.Height,
            RenderPassHandle: vulkanFramebuffer.Pass.RenderPass.Handle,
            Width: vulkanFramebuffer.Width
        ));
    }
    /// <inheritdoc/>
    public void BindDescriptorSet(nint deviceHandle, nint commandBufferHandle, nint pipelineLayoutHandle, nint descriptorSetHandle) {
        commandBufferRecordingApi.BindDescriptorSet(
            commandBufferHandle: commandBufferHandle,
            descriptorSetHandle: descriptorSetHandle,
            device: VulkanDeviceCommands.FromToken(token: deviceHandle),
            pipelineLayoutHandle: pipelineLayoutHandle
        );
    }
    /// <inheritdoc/>
    public void BindGraphicsPipeline(nint deviceHandle, nint commandBufferHandle, nint pipelineHandle) {
        commandBufferRecordingApi.BindGraphicsPipeline(
            commandBufferHandle: commandBufferHandle,
            device: VulkanDeviceCommands.FromToken(token: deviceHandle),
            pipelineHandle: pipelineHandle
        );
    }
    /// <inheritdoc/>
    /// <remarks>Vulkan takes the stride from the pipeline and the extent from the buffer, so only the handle reaches the
    /// command.</remarks>
    public void BindVertexBuffer(nint deviceHandle, nint commandBufferHandle, nint bufferHandle, ulong sizeBytes, uint strideBytes) {
        commandBufferRecordingApi.BindVertexBuffer(
            commandBufferHandle: commandBufferHandle,
            device: VulkanDeviceCommands.FromToken(token: deviceHandle),
            vertexBufferBinding: new VulkanVertexBufferBinding(bufferHandle: bufferHandle)
        );
    }
    /// <inheritdoc/>
    /// <remarks>Vulkan reads indices to the end of the buffer, so the size does not reach the command.</remarks>
    public void BindIndexBuffer(nint deviceHandle, nint commandBufferHandle, nint bufferHandle, ulong offsetBytes, ulong sizeBytes, GpuIndexFormat format) =>
        commandBufferRecordingApi.BindIndexBuffer(
            bufferHandle: bufferHandle,
            commandBufferHandle: commandBufferHandle,
            device: VulkanDeviceCommands.FromToken(token: deviceHandle),
            indexType: format switch {
                GpuIndexFormat.UInt16 => 0U,
                GpuIndexFormat.UInt32 => 1U,
                _ => throw new ArgumentOutOfRangeException(
                    actualValue: format,
                    message: "The index format is not defined.",
                    paramName: nameof(format)
                ),
            },
            offsetBytes: offsetBytes
        );
    /// <inheritdoc/>
    public void DrawIndexed(nint deviceHandle, nint commandBufferHandle, uint indexCount) {
        ArgumentOutOfRangeException.ThrowIfZero(value: indexCount);
        commandBufferRecordingApi.DrawIndexed(
            commandBufferHandle: commandBufferHandle,
            device: VulkanDeviceCommands.FromToken(token: deviceHandle),
            firstIndex: 0,
            firstInstance: 0,
            indexCount: indexCount,
            instanceCount: 1,
            vertexOffset: 0
        );
    }
    /// <inheritdoc/>
    public void Draw(nint deviceHandle, nint commandBufferHandle, in GpuDrawParameters parameters) {
        commandBufferRecordingApi.Draw(
            commandBufferHandle: commandBufferHandle,
            device: VulkanDeviceCommands.FromToken(token: deviceHandle),
            firstInstance: parameters.FirstInstance,
            firstVertex: parameters.FirstVertex,
            instanceCount: parameters.InstanceCount,
            vertexCount: parameters.VertexCount
        );
    }
    /// <inheritdoc/>
    public void EndCommandBuffer(nint deviceHandle, nint commandBufferHandle) {
        commandBufferRecordingApi.EndCommandBuffer(
            commandBufferHandle: commandBufferHandle,
            device: VulkanDeviceCommands.FromToken(token: deviceHandle)
        ).ThrowIfFailed(operation: "vkEndCommandBuffer");
    }
    /// <inheritdoc/>
    public void EndDebugGroup(nint deviceHandle, nint commandBufferHandle) {
        commandBufferRecordingApi.EndDebugLabel(
            commandBufferHandle: commandBufferHandle,
            device: VulkanDeviceCommands.FromToken(token: deviceHandle)
        );
    }
    /// <inheritdoc/>
    public void EndRenderPass(nint deviceHandle, nint commandBufferHandle) {
        commandBufferRecordingApi.EndRenderPass(
            commandBufferHandle: commandBufferHandle,
            device: VulkanDeviceCommands.FromToken(token: deviceHandle)
        );
    }
    /// <inheritdoc/>
    public void PushConstants(nint deviceHandle, nint commandBufferHandle, nint pipelineLayoutHandle, GpuShaderStage stageFlags, uint offset, ReadOnlySpan<byte> data) {
        GpuPushConstantBinding.ValidateRange(
            stageFlags: stageFlags,
            offset: offset,
            dataLength: data.Length
        );
        commandBufferRecordingApi.PushConstants(
            commandBufferHandle: commandBufferHandle,
            data: data,
            device: VulkanDeviceCommands.FromToken(token: deviceHandle),
            offset: offset,
            pipelineLayoutHandle: pipelineLayoutHandle,
            stageFlags: ((uint)stageFlags)
        );
    }
    /// <inheritdoc/>
    public void SetScissor(nint deviceHandle, nint commandBufferHandle, int x, int y, uint width, uint height) {
        commandBufferRecordingApi.SetScissor(
            commandBufferHandle: commandBufferHandle,
            device: VulkanDeviceCommands.FromToken(token: deviceHandle),
            height: height,
            width: width,
            x: x,
            y: y
        );
    }
}
