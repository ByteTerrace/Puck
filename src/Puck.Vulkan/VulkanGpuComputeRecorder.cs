using Puck.Vulkan.Interfaces;

namespace Puck.Vulkan;

/// <summary>
/// Implements <see cref="IGpuComputeRecorder"/> for Vulkan by forwarding to
/// <see cref="IVulkanCommandBufferRecordingApi"/>, mapping the neutral <see cref="GpuImageLayout"/>,
/// <see cref="GpuComputeStage"/>, and <see cref="GpuComputeAccess"/> values to their Vulkan flags.
/// </summary>
public sealed class VulkanGpuComputeRecorder(IVulkanCommandBufferRecordingApi recordingApi) : IGpuComputeRecorder {
    /// <inheritdoc/>
    public void BeginCommandBuffer(nint deviceHandle, nint commandBufferHandle) =>
        recordingApi.BeginCommandBuffer(commandBufferHandle: commandBufferHandle, deviceHandle: deviceHandle).ThrowIfFailed(operation: "vkBeginCommandBuffer");
    /// <inheritdoc/>
    public void EndCommandBuffer(nint deviceHandle, nint commandBufferHandle) =>
        recordingApi.EndCommandBuffer(commandBufferHandle: commandBufferHandle, deviceHandle: deviceHandle).ThrowIfFailed(operation: "vkEndCommandBuffer");
    /// <inheritdoc/>
    public void BeginDebugGroup(nint deviceHandle, nint commandBufferHandle, string label) =>
        recordingApi.BeginDebugLabel(commandBufferHandle: commandBufferHandle, deviceHandle: deviceHandle, label: label);
    /// <inheritdoc/>
    public void EndDebugGroup(nint deviceHandle, nint commandBufferHandle) =>
        recordingApi.EndDebugLabel(commandBufferHandle: commandBufferHandle, deviceHandle: deviceHandle);
    /// <inheritdoc/>
    public void BindComputePipeline(nint deviceHandle, nint commandBufferHandle, nint pipelineHandle) =>
        recordingApi.BindComputePipeline(commandBufferHandle: commandBufferHandle, deviceHandle: deviceHandle, pipelineHandle: pipelineHandle);
    /// <inheritdoc/>
    public void BindComputeDescriptorSet(nint deviceHandle, nint commandBufferHandle, nint pipelineLayoutHandle, nint descriptorSetHandle) =>
        recordingApi.BindComputeDescriptorSets(commandBufferHandle: commandBufferHandle, descriptorSetHandles: [descriptorSetHandle], deviceHandle: deviceHandle, pipelineLayoutHandle: pipelineLayoutHandle);
    /// <inheritdoc/>
    public void PushConstants(nint deviceHandle, nint commandBufferHandle, nint pipelineLayoutHandle, GpuShaderStage stageFlags, uint offset, ReadOnlySpan<byte> data) =>
        recordingApi.PushConstants(commandBufferHandle: commandBufferHandle, data: data, deviceHandle: deviceHandle, offset: offset, pipelineLayoutHandle: pipelineLayoutHandle, stageFlags: (uint)stageFlags);
    /// <inheritdoc/>
    public void Dispatch(nint deviceHandle, nint commandBufferHandle, uint groupCountX, uint groupCountY, uint groupCountZ) =>
        recordingApi.Dispatch(commandBufferHandle: commandBufferHandle, deviceHandle: deviceHandle, groupCountX: groupCountX, groupCountY: groupCountY, groupCountZ: groupCountZ);
    /// <inheritdoc/>
    public void DispatchIndirect(nint deviceHandle, nint commandBufferHandle, nint argumentBufferHandle, ulong argumentBufferOffset) =>
        recordingApi.DispatchIndirect(bufferHandle: argumentBufferHandle, commandBufferHandle: commandBufferHandle, deviceHandle: deviceHandle, offset: argumentBufferOffset);
    /// <inheritdoc/>
    public void TransitionImageLayout(nint deviceHandle, nint commandBufferHandle, nint imageHandle, GpuImageLayout oldLayout, GpuImageLayout newLayout, GpuComputeAccess sourceAccessMask, GpuComputeAccess destinationAccessMask, GpuComputeStage sourceStageMask, GpuComputeStage destinationStageMask) =>
        recordingApi.TransitionImageLayout(
            baseMipLevel: 0,
            commandBufferHandle: commandBufferHandle,
            destinationAccessMask: ToVulkanAccess(access: destinationAccessMask),
            destinationStageMask: ToVulkanStage(stage: destinationStageMask),
            deviceHandle: deviceHandle,
            imageHandle: imageHandle,
            mipLevelCount: 1,
            newLayout: ToVulkanLayout(layout: newLayout),
            oldLayout: ToVulkanLayout(layout: oldLayout),
            sourceAccessMask: ToVulkanAccess(access: sourceAccessMask),
            sourceStageMask: ToVulkanStage(stage: sourceStageMask)
        );
    /// <inheritdoc/>
    public void MemoryBarrier(nint deviceHandle, nint commandBufferHandle, GpuComputeAccess sourceAccessMask, GpuComputeAccess destinationAccessMask, GpuComputeStage sourceStageMask, GpuComputeStage destinationStageMask) =>
        recordingApi.PipelineMemoryBarrier(
            commandBufferHandle: commandBufferHandle,
            destinationAccessMask: ToVulkanAccess(access: destinationAccessMask),
            destinationStageMask: ToVulkanStage(stage: destinationStageMask),
            deviceHandle: deviceHandle,
            sourceAccessMask: ToVulkanAccess(access: sourceAccessMask),
            sourceStageMask: ToVulkanStage(stage: sourceStageMask)
        );
    /// <inheritdoc/>
    public void TransitionBuffer(nint deviceHandle, nint commandBufferHandle, nint bufferHandle, GpuComputeAccess sourceAccessMask, GpuComputeAccess destinationAccessMask, GpuComputeStage sourceStageMask, GpuComputeStage destinationStageMask) =>
        // Vulkan needs no per-buffer transition for an indirect read — a global memory barrier over the same
        // access/stage scopes (carrying VK_ACCESS_INDIRECT_COMMAND_READ_BIT / VK_PIPELINE_STAGE_DRAW_INDIRECT_BIT)
        // already orders the producing write before the indirect fetch. (Direct3D 12 is where a per-resource barrier matters.)
        recordingApi.PipelineMemoryBarrier(
            commandBufferHandle: commandBufferHandle,
            destinationAccessMask: ToVulkanAccess(access: destinationAccessMask),
            destinationStageMask: ToVulkanStage(stage: destinationStageMask),
            deviceHandle: deviceHandle,
            sourceAccessMask: ToVulkanAccess(access: sourceAccessMask),
            sourceStageMask: ToVulkanStage(stage: sourceStageMask)
        );

    private static uint ToVulkanAccess(GpuComputeAccess access) {
        var result = 0U;

        if (0 != (access & GpuComputeAccess.ShaderRead)) {
            result |= VulkanAccessFlags.ShaderRead;
        }

        if (0 != (access & GpuComputeAccess.ShaderWrite)) {
            result |= VulkanAccessFlags.ShaderWrite;
        }

        if (0 != (access & GpuComputeAccess.IndirectCommandRead)) {
            result |= VulkanAccessFlags.IndirectCommandRead;
        }

        return result;
    }
    private static uint ToVulkanLayout(GpuImageLayout layout) {
        return layout switch {
            GpuImageLayout.General => VulkanImageLayout.General,
            GpuImageLayout.ShaderReadOnly => VulkanImageLayout.ShaderReadOnlyOptimal,
            // The cross-Vulkan external handoff layout an importing instance re-transitions from.
            GpuImageLayout.External => VulkanImageLayout.General,
            _ => VulkanImageLayout.Undefined,
        };
    }
    private static uint ToVulkanStage(GpuComputeStage stage) {
        var result = 0U;

        if (0 != (stage & GpuComputeStage.TopOfPipe)) {
            result |= VulkanPipelineStageFlags.TopOfPipe;
        }

        if (0 != (stage & GpuComputeStage.ComputeShader)) {
            result |= VulkanPipelineStageFlags.ComputeShader;
        }

        if (0 != (stage & GpuComputeStage.FragmentShader)) {
            result |= VulkanPipelineStageFlags.FragmentShader;
        }

        if (0 != (stage & GpuComputeStage.DrawIndirect)) {
            result |= VulkanPipelineStageFlags.DrawIndirect;
        }

        return result;
    }
}
