using Puck.Vulkan.Interfaces;
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
            deviceHandle: deviceHandle
        ).ThrowIfFailed(operation: "vkBeginCommandBuffer");
    }
    /// <inheritdoc/>
    public void EndCommandBuffer(nint deviceHandle, nint commandBufferHandle) {
        commandBufferRecordingApi.EndCommandBuffer(
            commandBufferHandle: commandBufferHandle,
            deviceHandle: deviceHandle
        ).ThrowIfFailed(operation: "vkEndCommandBuffer");
    }
    /// <inheritdoc/>
    public void BeginDebugGroup(nint deviceHandle, nint commandBufferHandle, string label) {
        commandBufferRecordingApi.BeginDebugLabel(
            commandBufferHandle: commandBufferHandle,
            deviceHandle: deviceHandle,
            label: label
        );
    }
    /// <inheritdoc/>
    public void EndDebugGroup(nint deviceHandle, nint commandBufferHandle) {
        commandBufferRecordingApi.EndDebugLabel(
            commandBufferHandle: commandBufferHandle,
            deviceHandle: deviceHandle
        );
    }
    /// <inheritdoc/>
    public void BeginRenderPass(nint deviceHandle, nint commandBufferHandle, nint renderPassHandle, nint framebufferHandle, uint width, uint height) {
        commandBufferRecordingApi.StartRenderPass(request: new VulkanCommandBufferRecordRequest(
            CommandBufferHandle: commandBufferHandle,
            DeviceHandle: deviceHandle,
            FramebufferHandle: framebufferHandle,
            Height: height,
            RenderPassHandle: renderPassHandle,
            Width: width
        ));
    }
    /// <inheritdoc/>
    public void EndRenderPass(nint deviceHandle, nint commandBufferHandle) {
        commandBufferRecordingApi.EndRenderPass(
            commandBufferHandle: commandBufferHandle,
            deviceHandle: deviceHandle
        );
    }
    /// <inheritdoc/>
    public void BindGraphicsPipeline(nint deviceHandle, nint commandBufferHandle, nint pipelineHandle) {
        commandBufferRecordingApi.BindGraphicsPipeline(
            commandBufferHandle: commandBufferHandle,
            deviceHandle: deviceHandle,
            pipelineHandle: pipelineHandle
        );
    }
    /// <inheritdoc/>
    public void BindVertexBuffer(nint deviceHandle, nint commandBufferHandle, nint vertexBufferHandle) {
        commandBufferRecordingApi.BindVertexBuffer(
            commandBufferHandle: commandBufferHandle,
            deviceHandle: deviceHandle,
            vertexBufferBinding: new VulkanVertexBufferBinding(bufferHandle: vertexBufferHandle)
        );
    }
    /// <inheritdoc/>
    public void BindDescriptorSet(nint deviceHandle, nint commandBufferHandle, nint pipelineLayoutHandle, nint descriptorSetHandle) {
        commandBufferRecordingApi.BindDescriptorSet(
            commandBufferHandle: commandBufferHandle,
            descriptorSetHandle: descriptorSetHandle,
            deviceHandle: deviceHandle,
            pipelineLayoutHandle: pipelineLayoutHandle
        );
    }
    /// <inheritdoc/>
    public void PushConstants(nint deviceHandle, nint commandBufferHandle, nint pipelineLayoutHandle, GpuShaderStage stageFlags, uint offset, ReadOnlySpan<byte> data) {
        commandBufferRecordingApi.PushConstants(
            commandBufferHandle: commandBufferHandle,
            data: data,
            deviceHandle: deviceHandle,
            offset: offset,
            pipelineLayoutHandle: pipelineLayoutHandle,
            stageFlags: (uint)stageFlags
        );
    }
    /// <inheritdoc/>
    public void SetScissor(nint deviceHandle, nint commandBufferHandle, int x, int y, uint width, uint height) {
        commandBufferRecordingApi.SetScissor(
            commandBufferHandle: commandBufferHandle,
            deviceHandle: deviceHandle,
            height: height,
            width: width,
            x: x,
            y: y
        );
    }
    /// <inheritdoc/>
    public void Draw(nint deviceHandle, nint commandBufferHandle, uint vertexCount, uint instanceCount, uint firstVertex, uint firstInstance) {
        commandBufferRecordingApi.Draw(
            commandBufferHandle: commandBufferHandle,
            deviceHandle: deviceHandle,
            firstInstance: firstInstance,
            firstVertex: firstVertex,
            instanceCount: instanceCount,
            vertexCount: vertexCount
        );
    }
}
