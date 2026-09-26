using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan;

/// <summary>
/// Implements <see cref="IGpuRecorder"/> for Vulkan by forwarding to <see cref="IVulkanCommandBufferRecordingApi"/>
/// against the current logical device of its device context, mapping the neutral <see cref="GpuImageLayout"/>,
/// <see cref="GpuStage"/>, and <see cref="GpuAccess"/> values to their Vulkan flags. Graphics pipelines
/// take their viewport and scissor dynamically: a render pass sets both to its area, with a negative-height viewport
/// that points clip-space +y at the top of the area, as Direct3D 12 does.
/// </summary>
/// <param name="deviceContext">The device context whose current logical device every command is recorded against; a
/// device recreated after a loss is picked up by the next command.</param>
/// <param name="recordingApi">The native recording API.</param>
public sealed class VulkanGpuRecorder(IVulkanDeviceContext deviceContext, IVulkanCommandBufferRecordingApi recordingApi) : IGpuRecorder {
    private VulkanDeviceCommands Device => deviceContext.LogicalDevice.Commands;

    private static uint ToVulkanAccess(GpuAccess access) {
        var result = 0U;

        if (0 != (access & GpuAccess.ShaderRead)) {
            result |= VulkanAccessFlags.ShaderRead;
        }

        if (0 != (access & GpuAccess.ShaderWrite)) {
            result |= VulkanAccessFlags.ShaderWrite;
        }

        if (0 != (access & GpuAccess.IndirectCommandRead)) {
            result |= VulkanAccessFlags.IndirectCommandRead;
        }

        if (0 != (access & GpuAccess.TransferWrite)) {
            result |= VulkanAccessFlags.TransferWrite;
        }

        if (0 != (access & GpuAccess.ColorAttachmentWrite)) {
            result |= VulkanAccessFlags.ColorAttachmentWrite;
        }

        if (0 != (access & GpuAccess.ColorAttachmentRead)) {
            result |= VulkanAccessFlags.ColorAttachmentRead;
        }

        if (0 != (access & GpuAccess.DepthAttachmentRead)) {
            result |= VulkanAccessFlags.DepthStencilAttachmentRead;
        }

        if (0 != (access & GpuAccess.DepthAttachmentWrite)) {
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
    private static uint ToVulkanStage(GpuStage stage) {
        var result = 0U;

        if (0 != (stage & GpuStage.TopOfPipe)) {
            result |= VulkanPipelineStageFlags.TopOfPipe;
        }

        if (0 != (stage & GpuStage.ComputeShader)) {
            result |= VulkanPipelineStageFlags.ComputeShader;
        }

        if (0 != (stage & GpuStage.FragmentShader)) {
            result |= VulkanPipelineStageFlags.FragmentShader;
        }

        if (0 != (stage & GpuStage.DrawIndirect)) {
            result |= VulkanPipelineStageFlags.DrawIndirect;
        }

        if (0 != (stage & GpuStage.Transfer)) {
            result |= VulkanPipelineStageFlags.Transfer;
        }

        if (0 != (stage & GpuStage.ColorAttachmentOutput)) {
            result |= VulkanPipelineStageFlags.ColorAttachmentOutput;
        }

        if (0 != (stage & GpuStage.FragmentTests)) {
            result |= VulkanPipelineStageFlags.EarlyFragmentTests | VulkanPipelineStageFlags.LateFragmentTests;
        }

        if (0 != (stage & GpuStage.VertexShader)) {
            result |= VulkanPipelineStageFlags.VertexShader;
        }

        return result;
    }

    /// <inheritdoc/>
    public void BeginCommandBuffer(nint commandBufferHandle) =>
        recordingApi.BeginCommandBuffer(
            commandBufferHandle: commandBufferHandle,
            device: Device
        ).ThrowIfFailed(operation: "vkBeginCommandBuffer");
    /// <inheritdoc/>
    public void BeginDebugGroup(nint commandBufferHandle, string label) =>
        recordingApi.BeginDebugLabel(
            commandBufferHandle: commandBufferHandle,
            device: Device,
            label: label
        );
    /// <inheritdoc/>
    /// <remarks>The group is the set number, <c>firstSet</c>. A set's own group is the one
    /// <see cref="VulkanLogicalDevice.SetGroups"/> recorded when it was allocated.</remarks>
    public void BindDescriptorSet(nint commandBufferHandle, GpuBindPoint bindPoint, nint pipelineLayoutHandle, uint group, nint descriptorSetHandle) {
        var logicalDevice = deviceContext.LogicalDevice;
        var own = logicalDevice.SetGroups.GroupOf(setHandle: descriptorSetHandle);

        if (own != group) {
            throw new InvalidOperationException(message: $"A set of group {own} is bound at group {group}; a set binds only at its own group.");
        }

        if (bindPoint == GpuBindPoint.Graphics) {
            recordingApi.BindDescriptorSet(
                commandBufferHandle: commandBufferHandle,
                descriptorSetHandle: descriptorSetHandle,
                device: logicalDevice.Commands,
                firstSet: group,
                pipelineLayoutHandle: pipelineLayoutHandle
            );

            return;
        }

        // A one-element stack span rather than a heap array: the SDF engine binds a compute set several times a frame,
        // and the native call reads the span only for its duration.
        ReadOnlySpan<nint> descriptorSetHandles = stackalloc nint[] { descriptorSetHandle };

        recordingApi.BindComputeDescriptorSets(
            commandBufferHandle: commandBufferHandle,
            descriptorSetHandles: descriptorSetHandles,
            device: logicalDevice.Commands,
            firstSet: group,
            pipelineLayoutHandle: pipelineLayoutHandle
        );
    }
    /// <inheritdoc/>
    public void BindPipeline(nint commandBufferHandle, GpuBindPoint bindPoint, nint pipelineHandle) {
        if (bindPoint == GpuBindPoint.Graphics) {
            recordingApi.BindGraphicsPipeline(
                commandBufferHandle: commandBufferHandle,
                device: Device,
                pipelineHandle: pipelineHandle
            );
        } else {
            recordingApi.BindComputePipeline(
                commandBufferHandle: commandBufferHandle,
                device: Device,
                pipelineHandle: pipelineHandle
            );
        }
    }
    /// <inheritdoc/>
    public void ClearStorageBuffer(nint commandBufferHandle, nint bufferHandle, ulong sizeBytes) {
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
            device: Device,
            sizeBytes: sizeBytes
        );
        // vkCmdFillBuffer writes in the transfer stage. The neutral compute barrier vocabulary has no transfer
        // stage, so make this backend handoff explicit before the first compute shader access.
        recordingApi.PipelineMemoryBarrier(
            commandBufferHandle: commandBufferHandle,
            destinationAccessMask: VulkanAccessFlags.ShaderRead | VulkanAccessFlags.ShaderWrite,
            destinationStageMask: VulkanPipelineStageFlags.ComputeShader,
            device: Device,
            sourceAccessMask: VulkanAccessFlags.TransferWrite,
            sourceStageMask: VulkanPipelineStageFlags.Transfer
        );
    }
    /// <inheritdoc/>
    public void ClearStorageImage(nint commandBufferHandle, nint imageHandle, GpuPixelFormat format) {
        if (format is not (GpuPixelFormat.R8G8B8A8Unorm or GpuPixelFormat.B8G8R8A8Unorm or GpuPixelFormat.R16G16B16A16Float or GpuPixelFormat.R32G32B32A32Float)) {
            throw new ArgumentOutOfRangeException(
                nameof(format),
                format,
                "Storage-image clear requires a supported color format."
            );
        }
        recordingApi.ClearColorImage(
            device: Device,
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
    public void Dispatch(nint commandBufferHandle, uint groupCountX, uint groupCountY, uint groupCountZ) =>
        recordingApi.Dispatch(
            commandBufferHandle: commandBufferHandle,
            device: Device,
            groupCountX: groupCountX,
            groupCountY: groupCountY,
            groupCountZ: groupCountZ
        );
    /// <inheritdoc/>
    public void DispatchIndirect(nint commandBufferHandle, nint argumentBufferHandle, ulong argumentBufferOffset) =>
        recordingApi.DispatchIndirect(
            bufferHandle: argumentBufferHandle,
            commandBufferHandle: commandBufferHandle,
            device: Device,
            offset: argumentBufferOffset
        );
    /// <inheritdoc/>
    public void EndCommandBuffer(nint commandBufferHandle) =>
        recordingApi.EndCommandBuffer(
            commandBufferHandle: commandBufferHandle,
            device: Device
        ).ThrowIfFailed(operation: "vkEndCommandBuffer");
    /// <inheritdoc/>
    public void EndDebugGroup(nint commandBufferHandle) =>
        recordingApi.EndDebugLabel(
            commandBufferHandle: commandBufferHandle,
            device: Device
        );
    /// <inheritdoc/>
    public void MemoryBarrier(nint commandBufferHandle, GpuAccess sourceAccessMask, GpuAccess destinationAccessMask, GpuStage sourceStageMask, GpuStage destinationStageMask) =>
        recordingApi.PipelineMemoryBarrier(
            commandBufferHandle: commandBufferHandle,
            destinationAccessMask: ToVulkanAccess(access: destinationAccessMask),
            destinationStageMask: ToVulkanStage(stage: destinationStageMask),
            device: Device,
            sourceAccessMask: ToVulkanAccess(access: sourceAccessMask),
            sourceStageMask: ToVulkanStage(stage: sourceStageMask)
        );
    /// <inheritdoc/>
    public void PushConstants(nint commandBufferHandle, GpuBindPoint bindPoint, nint pipelineLayoutHandle, GpuShaderStage stageFlags, uint offset, ReadOnlySpan<byte> data) {
        GpuPushConstantBinding.ValidateRange(
            stageFlags: stageFlags,
            offset: offset,
            dataLength: data.Length
        );
        recordingApi.PushConstants(
            commandBufferHandle: commandBufferHandle,
            data: data,
            device: Device,
            offset: offset,
            pipelineLayoutHandle: pipelineLayoutHandle,
            stageFlags: ((uint)stageFlags)
        );
    }
    /// <inheritdoc/>
    /// <remarks>Records a buffer memory barrier over the whole buffer; Vulkan buffers carry no layout, so the accesses
    /// and stages are the whole transition.</remarks>
    public void TransitionBuffer(nint commandBufferHandle, nint bufferHandle, GpuAccess sourceAccessMask, GpuAccess destinationAccessMask, GpuStage sourceStageMask, GpuStage destinationStageMask) =>
        recordingApi.PipelineBufferBarrier(
            bufferHandle: bufferHandle,
            commandBufferHandle: commandBufferHandle,
            destinationAccessMask: ToVulkanAccess(access: destinationAccessMask),
            destinationStageMask: ToVulkanStage(stage: destinationStageMask),
            device: Device,
            sourceAccessMask: ToVulkanAccess(access: sourceAccessMask),
            sourceStageMask: ToVulkanStage(stage: sourceStageMask)
        );
    /// <inheritdoc/>
    /// <remarks>A transition into or out of <see cref="GpuImageLayout.DepthAttachment"/> covers the depth aspect, since only
    /// a depth image takes that layout; every other transition covers the color aspect.</remarks>
    public void TransitionImageLayout(nint commandBufferHandle, nint imageHandle, GpuImageLayout oldLayout, GpuImageLayout newLayout, GpuAccess sourceAccessMask, GpuAccess destinationAccessMask, GpuStage sourceStageMask, GpuStage destinationStageMask) =>
        recordingApi.TransitionImageLayout(
            aspectMask: (((oldLayout == GpuImageLayout.DepthAttachment) || (newLayout == GpuImageLayout.DepthAttachment))
                ? VulkanGpuFormats.DepthAspect
                : VulkanGpuFormats.ColorAspect),
            baseMipLevel: 0,
            commandBufferHandle: commandBufferHandle,
            destinationAccessMask: ToVulkanAccess(access: destinationAccessMask),
            destinationStageMask: ToVulkanStage(stage: destinationStageMask),
            device: Device,
            imageHandle: imageHandle,
            mipLevelCount: 1,
            newLayout: ToVulkanLayout(layout: newLayout),
            oldLayout: ToVulkanLayout(layout: oldLayout),
            sourceAccessMask: ToVulkanAccess(access: sourceAccessMask),
            sourceStageMask: ToVulkanStage(stage: sourceStageMask)
        );
    /// <inheritdoc/>
    public void BeginRenderPass(nint commandBufferHandle, IGpuFramebuffer framebuffer, GpuPixelRect? area = null) {
        var vulkanFramebuffer = ((VulkanGpuFramebuffer)framebuffer);
        var drawn = GpuFramebuffers.ResolveArea(
            area: area,
            framebuffer: framebuffer
        );
        var device = Device;

        recordingApi.StartRenderPass(request: new VulkanCommandBufferRecordRequest(
            ClearValues: vulkanFramebuffer.Pass.ClearValues,
            CommandBufferHandle: commandBufferHandle,
            Device: device,
            FramebufferHandle: vulkanFramebuffer.Handle,
            Height: drawn.Height,
            RenderPassHandle: vulkanFramebuffer.Pass.RenderPass.Handle,
            Width: drawn.Width,
            X: drawn.X,
            Y: drawn.Y
        ));
        recordingApi.SetViewport(
            commandBufferHandle: commandBufferHandle,
            device: device,
            viewport: new VkViewport(
                height: -((float)drawn.Height),
                maxDepth: 1f,
                minDepth: 0f,
                width: drawn.Width,
                x: drawn.X,
                y: (((float)drawn.Y) + drawn.Height)
            )
        );
        recordingApi.SetScissor(
            commandBufferHandle: commandBufferHandle,
            device: device,
            height: drawn.Height,
            width: drawn.Width,
            x: drawn.X,
            y: drawn.Y
        );
    }
    /// <inheritdoc/>
    public void EndRenderPass(nint commandBufferHandle) =>
        recordingApi.EndRenderPass(
            commandBufferHandle: commandBufferHandle,
            device: Device
        );
    /// <inheritdoc/>
    /// <remarks>Vulkan takes the stride from the pipeline and the extent from the buffer, so only the handle reaches the
    /// command.</remarks>
    public void BindVertexBuffer(nint commandBufferHandle, nint bufferHandle, ulong sizeBytes, uint strideBytes) =>
        recordingApi.BindVertexBuffer(
            commandBufferHandle: commandBufferHandle,
            device: Device,
            vertexBufferBinding: new VulkanVertexBufferBinding(bufferHandle: bufferHandle)
        );
    /// <inheritdoc/>
    /// <remarks>Vulkan reads indices to the end of the buffer, so the size does not reach the command.</remarks>
    public void BindIndexBuffer(nint commandBufferHandle, nint bufferHandle, ulong offsetBytes, ulong sizeBytes, GpuIndexFormat format) =>
        recordingApi.BindIndexBuffer(
            bufferHandle: bufferHandle,
            commandBufferHandle: commandBufferHandle,
            device: Device,
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
    public void SetScissor(nint commandBufferHandle, GpuPixelRect rect) =>
        recordingApi.SetScissor(
            commandBufferHandle: commandBufferHandle,
            device: Device,
            height: rect.Height,
            width: rect.Width,
            x: rect.X,
            y: rect.Y
        );
    /// <inheritdoc/>
    public void Draw(nint commandBufferHandle, in GpuDrawParameters parameters) =>
        recordingApi.Draw(
            commandBufferHandle: commandBufferHandle,
            device: Device,
            firstInstance: parameters.FirstInstance,
            firstVertex: parameters.FirstVertex,
            instanceCount: parameters.InstanceCount,
            vertexCount: parameters.VertexCount
        );
    /// <inheritdoc/>
    public void DrawIndexed(nint commandBufferHandle, uint indexCount) {
        ArgumentOutOfRangeException.ThrowIfZero(value: indexCount);
        recordingApi.DrawIndexed(
            commandBufferHandle: commandBufferHandle,
            device: Device,
            firstIndex: 0,
            firstInstance: 0,
            indexCount: indexCount,
            instanceCount: 1,
            vertexOffset: 0
        );
    }
}
