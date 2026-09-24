using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;

namespace Puck.Vulkan;

/// <summary>
/// Implements <see cref="IGpuComputeRecorder"/> for Vulkan by forwarding to
/// <see cref="IVulkanCommandBufferRecordingApi"/>, mapping the neutral <see cref="GpuImageLayout"/>,
/// <see cref="GpuComputeStage"/>, and <see cref="GpuComputeAccess"/> values to their Vulkan flags.
/// </summary>
public sealed class VulkanGpuComputeRecorder(IVulkanCommandBufferRecordingApi recordingApi) : IGpuComputeRecorder, IGpuImageInitializationRecorder, IGpuBufferInitializationRecorder {
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

        if (0 != (access & GpuComputeAccess.TransferWrite)) {
            result |= VulkanAccessFlags.TransferWrite;
        }

        if (0 != (access & GpuComputeAccess.ColorAttachmentWrite)) {
            result |= VulkanAccessFlags.ColorAttachmentWrite;
        }

        if (0 != (access & GpuComputeAccess.ColorAttachmentRead)) {
            result |= VulkanAccessFlags.ColorAttachmentRead;
        }

        if (0 != (access & GpuComputeAccess.DepthAttachmentRead)) {
            result |= VulkanAccessFlags.DepthStencilAttachmentRead;
        }

        if (0 != (access & GpuComputeAccess.DepthAttachmentWrite)) {
            result |= VulkanAccessFlags.DepthStencilAttachmentWrite;
        }

        return result;
    }
    private static uint ToVulkanLayout(GpuImageLayout layout) {
        return layout switch {
            GpuImageLayout.General => VulkanImageLayout.General,
            GpuImageLayout.ShaderReadOnly => VulkanImageLayout.ShaderReadOnlyOptimal,
            GpuImageLayout.RenderTarget => VulkanImageLayout.ColorAttachmentOptimal,
            GpuImageLayout.DepthAttachment => VulkanImageLayout.DepthStencilAttachmentOptimal,
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

        if (0 != (stage & GpuComputeStage.Transfer)) {
            result |= VulkanPipelineStageFlags.Transfer;
        }

        if (0 != (stage & GpuComputeStage.ColorAttachmentOutput)) {
            result |= VulkanPipelineStageFlags.ColorAttachmentOutput;
        }

        if (0 != (stage & GpuComputeStage.FragmentTests)) {
            result |= VulkanPipelineStageFlags.EarlyFragmentTests | VulkanPipelineStageFlags.LateFragmentTests;
        }

        return result;
    }

    /// <inheritdoc/>
    public void BeginCommandBuffer(nint deviceHandle, nint commandBufferHandle) =>
        recordingApi.BeginCommandBuffer(
            commandBufferHandle: commandBufferHandle,
            device: VulkanDeviceCommands.FromToken(token: deviceHandle)
        ).ThrowIfFailed(operation: "vkBeginCommandBuffer");
    /// <inheritdoc/>
    public void BeginDebugGroup(nint deviceHandle, nint commandBufferHandle, string label) =>
        recordingApi.BeginDebugLabel(
            commandBufferHandle: commandBufferHandle,
            device: VulkanDeviceCommands.FromToken(token: deviceHandle),
            label: label
        );
    /// <inheritdoc/>
    public void BindComputeDescriptorSet(nint deviceHandle, nint commandBufferHandle, nint pipelineLayoutHandle, nint descriptorSetHandle) {
        // stackalloc instead of a one-element heap array: this runs up to 8x per SdfWorldEngine.Record, every frame
        // (a live world uploads a new SdfProgram every frame). The native call (BindComputeDescriptorSets) only reads
        // the span during the call via `fixed`, so the stack lifetime is sufficient.
        ReadOnlySpan<nint> descriptorSetHandles = stackalloc nint[] { descriptorSetHandle };

        recordingApi.BindComputeDescriptorSets(
            commandBufferHandle: commandBufferHandle,
            descriptorSetHandles: descriptorSetHandles,
            device: VulkanDeviceCommands.FromToken(token: deviceHandle),
            pipelineLayoutHandle: pipelineLayoutHandle
        );
    }
    /// <inheritdoc/>
    public void BindComputePipeline(nint deviceHandle, nint commandBufferHandle, nint pipelineHandle) =>
        recordingApi.BindComputePipeline(
            commandBufferHandle: commandBufferHandle,
            device: VulkanDeviceCommands.FromToken(token: deviceHandle),
            pipelineHandle: pipelineHandle
        );
    /// <inheritdoc/>
    public void ClearStorageBuffer(nint deviceHandle, nint commandBufferHandle, nint bufferHandle, ulong sizeBytes) {
        ArgumentOutOfRangeException.ThrowIfZero(deviceHandle);
        ArgumentOutOfRangeException.ThrowIfZero(commandBufferHandle);
        ArgumentOutOfRangeException.ThrowIfZero(bufferHandle);
        if (
            (sizeBytes == 0) ||
            ((sizeBytes & 3) != 0)
        ) {
            throw new ArgumentOutOfRangeException(
                nameof(sizeBytes),
                sizeBytes,
                "Vulkan buffer fills require a positive size divisible by four."
            );
        }

        recordingApi.FillBuffer(
            bufferHandle: bufferHandle,
            commandBufferHandle: commandBufferHandle,
            device: VulkanDeviceCommands.FromToken(token: deviceHandle),
            sizeBytes: sizeBytes
        );
        // vkCmdFillBuffer writes in the transfer stage. The neutral compute barrier vocabulary has no transfer
        // stage, so make this backend handoff explicit before the first compute shader access.
        recordingApi.PipelineMemoryBarrier(
            commandBufferHandle: commandBufferHandle,
            destinationAccessMask: VulkanAccessFlags.ShaderRead | VulkanAccessFlags.ShaderWrite,
            destinationStageMask: VulkanPipelineStageFlags.ComputeShader,
            device: VulkanDeviceCommands.FromToken(token: deviceHandle),
            sourceAccessMask: VulkanAccessFlags.TransferWrite,
            sourceStageMask: VulkanPipelineStageFlags.Transfer
        );
    }
    /// <inheritdoc/>
    public void ClearStorageImage(nint deviceHandle, nint commandBufferHandle, nint imageHandle, GpuPixelFormat format) {
        if (format is not (GpuPixelFormat.R8G8B8A8Unorm or GpuPixelFormat.B8G8R8A8Unorm or GpuPixelFormat.R16G16B16A16Float or GpuPixelFormat.R32G32B32A32Float)) {
            throw new ArgumentOutOfRangeException(
                nameof(format),
                format,
                "Storage-image clear requires a supported color format."
            );
        }
        recordingApi.ClearColorImage(
            device: VulkanDeviceCommands.FromToken(token: deviceHandle),
            commandBufferHandle: commandBufferHandle,
            imageHandle: imageHandle,
            imageLayout: ToVulkanLayout(layout: GpuImageLayout.General),
            red: 0f,
            green: 0f,
            blue: 0f,
            alpha: 0f
        );
    }
    /// <inheritdoc/>
    public void Dispatch(nint deviceHandle, nint commandBufferHandle, uint groupCountX, uint groupCountY, uint groupCountZ) =>
        recordingApi.Dispatch(
            commandBufferHandle: commandBufferHandle,
            device: VulkanDeviceCommands.FromToken(token: deviceHandle),
            groupCountX: groupCountX,
            groupCountY: groupCountY,
            groupCountZ: groupCountZ
        );
    /// <inheritdoc/>
    public void DispatchIndirect(nint deviceHandle, nint commandBufferHandle, nint argumentBufferHandle, ulong argumentBufferOffset) =>
        recordingApi.DispatchIndirect(
            bufferHandle: argumentBufferHandle,
            commandBufferHandle: commandBufferHandle,
            device: VulkanDeviceCommands.FromToken(token: deviceHandle),
            offset: argumentBufferOffset
        );
    /// <inheritdoc/>
    public void EndCommandBuffer(nint deviceHandle, nint commandBufferHandle) =>
        recordingApi.EndCommandBuffer(
            commandBufferHandle: commandBufferHandle,
            device: VulkanDeviceCommands.FromToken(token: deviceHandle)
        ).ThrowIfFailed(operation: "vkEndCommandBuffer");
    /// <inheritdoc/>
    public void EndDebugGroup(nint deviceHandle, nint commandBufferHandle) =>
        recordingApi.EndDebugLabel(
            commandBufferHandle: commandBufferHandle,
            device: VulkanDeviceCommands.FromToken(token: deviceHandle)
        );
    /// <inheritdoc/>
    public void MemoryBarrier(nint deviceHandle, nint commandBufferHandle, GpuComputeAccess sourceAccessMask, GpuComputeAccess destinationAccessMask, GpuComputeStage sourceStageMask, GpuComputeStage destinationStageMask) =>
        recordingApi.PipelineMemoryBarrier(
            commandBufferHandle: commandBufferHandle,
            destinationAccessMask: ToVulkanAccess(access: destinationAccessMask),
            destinationStageMask: ToVulkanStage(stage: destinationStageMask),
            device: VulkanDeviceCommands.FromToken(token: deviceHandle),
            sourceAccessMask: ToVulkanAccess(access: sourceAccessMask),
            sourceStageMask: ToVulkanStage(stage: sourceStageMask)
        );
    /// <inheritdoc/>
    public void PushConstants(nint deviceHandle, nint commandBufferHandle, nint pipelineLayoutHandle, GpuShaderStage stageFlags, uint offset, ReadOnlySpan<byte> data) {
        GpuPushConstantBinding.ValidateRange(
            stageFlags: stageFlags,
            offset: offset,
            dataLength: data.Length
        );
        recordingApi.PushConstants(
            commandBufferHandle: commandBufferHandle,
            data: data,
            device: VulkanDeviceCommands.FromToken(token: deviceHandle),
            offset: offset,
            pipelineLayoutHandle: pipelineLayoutHandle,
            stageFlags: ((uint)stageFlags)
        );
    }
    /// <inheritdoc/>
    /// <remarks>Records a buffer memory barrier over the whole buffer; Vulkan buffers carry no layout, so the accesses
    /// and stages are the whole transition.</remarks>
    public void TransitionBuffer(nint deviceHandle, nint commandBufferHandle, nint bufferHandle, GpuComputeAccess sourceAccessMask, GpuComputeAccess destinationAccessMask, GpuComputeStage sourceStageMask, GpuComputeStage destinationStageMask) =>
        recordingApi.PipelineBufferBarrier(
            bufferHandle: bufferHandle,
            commandBufferHandle: commandBufferHandle,
            destinationAccessMask: ToVulkanAccess(access: destinationAccessMask),
            destinationStageMask: ToVulkanStage(stage: destinationStageMask),
            device: VulkanDeviceCommands.FromToken(token: deviceHandle),
            sourceAccessMask: ToVulkanAccess(access: sourceAccessMask),
            sourceStageMask: ToVulkanStage(stage: sourceStageMask)
        );
    /// <inheritdoc/>
    /// <remarks>A transition into or out of <see cref="GpuImageLayout.DepthAttachment"/> covers the depth aspect, since only
    /// a depth image takes that layout; every other transition covers the color aspect.</remarks>
    public void TransitionImageLayout(nint deviceHandle, nint commandBufferHandle, nint imageHandle, GpuImageLayout oldLayout, GpuImageLayout newLayout, GpuComputeAccess sourceAccessMask, GpuComputeAccess destinationAccessMask, GpuComputeStage sourceStageMask, GpuComputeStage destinationStageMask) =>
        recordingApi.TransitionImageLayout(
            aspectMask: (((oldLayout == GpuImageLayout.DepthAttachment) || (newLayout == GpuImageLayout.DepthAttachment))
                ? VulkanGpuFormats.DepthAspect
                : VulkanGpuFormats.ColorAspect),
            baseMipLevel: 0,
            commandBufferHandle: commandBufferHandle,
            destinationAccessMask: ToVulkanAccess(access: destinationAccessMask),
            destinationStageMask: ToVulkanStage(stage: destinationStageMask),
            device: VulkanDeviceCommands.FromToken(token: deviceHandle),
            imageHandle: imageHandle,
            mipLevelCount: 1,
            newLayout: ToVulkanLayout(layout: newLayout),
            oldLayout: ToVulkanLayout(layout: oldLayout),
            sourceAccessMask: ToVulkanAccess(access: sourceAccessMask),
            sourceStageMask: ToVulkanStage(stage: sourceStageMask)
        );
}
