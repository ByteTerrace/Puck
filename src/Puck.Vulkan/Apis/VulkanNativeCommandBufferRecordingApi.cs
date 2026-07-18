using System.Runtime.InteropServices;
using System.Text;
using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan;

/// <summary>
/// The native implementation of <see cref="IVulkanCommandBufferRecordingApi"/>, marshaling to the
/// <c>vkCmd*</c> command-recording entry points resolved per device from the Vulkan loader.
/// </summary>
public unsafe sealed class VulkanNativeCommandBufferRecordingApi : IVulkanCommandBufferRecordingApi {
    private readonly IAllocator m_allocator;

    /// <summary>Initializes a new instance of the <see cref="VulkanNativeCommandBufferRecordingApi"/> class.</summary>
    /// <param name="allocator">The unmanaged allocator used to marshal native Vulkan structures.</param>
    /// <exception cref="ArgumentNullException"><paramref name="allocator"/> is <see langword="null"/>.</exception>
    public VulkanNativeCommandBufferRecordingApi(IAllocator allocator) {
        ArgumentNullException.ThrowIfNull(argument: allocator);

        m_allocator = allocator;
    }

    private const uint ComputePipelineBindPoint = 1;
    private const uint GraphicsPipelineBindPoint = 0;
    private const uint ImageAspectColorBit = 0x00000001;
    private const uint QueueFamilyIgnored = 0xFFFFFFFF;
    private const uint StructureTypeCommandBufferBeginInfo = 42;
    private const uint StructureTypeDebugUtilsLabel = 1000128002;
    private const uint StructureTypeImageMemoryBarrier = 45;
    private const uint StructureTypeMemoryBarrier = 46;
    private const uint StructureTypeRenderPassBeginInfo = 43;
    private const uint SubpassContentsInline = 0;

    private readonly Lock m_syncRoot = new();
    private unsafe delegate* unmanaged[Cdecl]<nint, byte*, nint> m_getDeviceProcAddr;

    /// <inheritdoc/>
    public VkResult BeginCommandBuffer(VulkanCommandBufferRecordRequest request) {
        ValidateRequest(request: request);

        var beginCommandBuffer = GetPointers(deviceHandle: request.DeviceHandle).BeginCommandBuffer;
        // No ONE_TIME_SUBMIT: recorded command buffers may be cached and resubmitted across
        // frames without re-recording, which that flag forbids.
        var beginInfo = new VkCommandBufferBeginInfo {
            Flags = 0,
            SType = StructureTypeCommandBufferBeginInfo,
        };

        return beginCommandBuffer(
            request.CommandBufferHandle,
            in beginInfo
        );
    }
    /// <inheritdoc/>
    public VkResult BeginCommandBuffer(nint deviceHandle, nint commandBufferHandle) {
        if (0 == deviceHandle) {
            throw new ArgumentException(
                message: "Vulkan logical-device handle must be non-zero.",
                paramName: nameof(deviceHandle)
            );
        }

        if (0 == commandBufferHandle) {
            throw new ArgumentException(
                message: "Vulkan command-buffer handle must be non-zero.",
                paramName: nameof(commandBufferHandle)
            );
        }

        var beginCommandBuffer = GetPointers(deviceHandle: deviceHandle).BeginCommandBuffer;
        var beginInfo = new VkCommandBufferBeginInfo {
            Flags = 0,
            SType = StructureTypeCommandBufferBeginInfo,
        };

        return beginCommandBuffer(
            commandBufferHandle,
            in beginInfo
        );
    }
    /// <inheritdoc/>
    public void BeginDebugLabel(nint deviceHandle, nint commandBufferHandle, string label) {
        ArgumentException.ThrowIfNullOrEmpty(argument: label);

        var beginLabel = GetPointers(deviceHandle: deviceHandle).CmdBeginDebugUtilsLabel;

        // VK_EXT_debug_utils absent (extension not enabled, or no capture layer resolving the entry point): no-op.
        if (beginLabel is null) {
            return;
        }

        var byteCount = Encoding.UTF8.GetByteCount(s: label);
        Span<byte> nameBytes = stackalloc byte[(byteCount + 1)];

        Encoding.UTF8.GetBytes(chars: label, bytes: nameBytes);
        nameBytes[byteCount] = 0;

        fixed (byte* labelName = nameBytes) {
            var info = new VkDebugUtilsLabelExt {
                LabelName = labelName,
                StructureType = StructureTypeDebugUtilsLabel,
            };

            beginLabel(commandBufferHandle, in info);
        }
    }
    /// <inheritdoc/>
    public void EndDebugLabel(nint deviceHandle, nint commandBufferHandle) {
        var endLabel = GetPointers(deviceHandle: deviceHandle).CmdEndDebugUtilsLabel;

        // VK_EXT_debug_utils absent: no-op (balances a BeginDebugLabel that also no-oped).
        if (endLabel is not null) {
            endLabel(commandBufferHandle);
        }
    }
    /// <inheritdoc/>
    public void BindGraphicsPipeline(nint deviceHandle, nint commandBufferHandle, nint pipelineHandle) {
        if (0 == deviceHandle) {
            throw new ArgumentException(
                message: "Vulkan logical-device handle must be non-zero.",
                paramName: nameof(deviceHandle)
            );
        }

        if (0 == commandBufferHandle) {
            throw new ArgumentException(
                message: "Vulkan command-buffer handle must be non-zero.",
                paramName: nameof(commandBufferHandle)
            );
        }

        if (0 == pipelineHandle) {
            throw new ArgumentException(
                message: "Vulkan graphics-pipeline handle must be non-zero.",
                paramName: nameof(pipelineHandle)
            );
        }

        var bindPipeline = GetPointers(deviceHandle: deviceHandle).CmdBindPipeline;

        bindPipeline(
            commandBufferHandle,
            GraphicsPipelineBindPoint,
            pipelineHandle
        );
    }
    /// <inheritdoc/>
    public void BindVertexBuffer(nint deviceHandle, nint commandBufferHandle, VulkanVertexBufferBinding vertexBufferBinding) {
        if (0 == deviceHandle) {
            throw new ArgumentException(
                message: "Vulkan logical-device handle must be non-zero.",
                paramName: nameof(deviceHandle)
            );
        }

        if (0 == commandBufferHandle) {
            throw new ArgumentException(
                message: "Vulkan command-buffer handle must be non-zero.",
                paramName: nameof(commandBufferHandle)
            );
        }

        // vkCmdBindVertexBuffers copies both arrays during the call, so stack storage
        // suffices (same pattern as BindDescriptorSet) — this runs per draw per
        // re-record, where the previous array + pin pair was pure heap churn.
        var bindVertexBuffers = GetPointers(deviceHandle: deviceHandle).CmdBindVertexBuffers;
        var bufferHandles = stackalloc nint[1];

        bufferHandles[0] = vertexBufferBinding.BufferHandle;
        var offsets = stackalloc ulong[1];

        offsets[0] = vertexBufferBinding.Offset;
        bindVertexBuffers(
            commandBufferHandle,
            0,
            1,
            (nint)bufferHandles,
            (nint)offsets
        );
    }
    /// <inheritdoc/>
    public void BindDescriptorSet(nint deviceHandle, nint commandBufferHandle, nint pipelineLayoutHandle, nint descriptorSetHandle) {
        if (0 == deviceHandle) {
            throw new ArgumentException(
                message: "Vulkan logical-device handle must be non-zero.",
                paramName: nameof(deviceHandle)
            );
        }

        if (0 == commandBufferHandle) {
            throw new ArgumentException(
                message: "Vulkan command-buffer handle must be non-zero.",
                paramName: nameof(commandBufferHandle)
            );
        }

        if (0 == pipelineLayoutHandle) {
            throw new ArgumentException(
                message: "Vulkan pipeline-layout handle must be non-zero.",
                paramName: nameof(pipelineLayoutHandle)
            );
        }

        if (0 == descriptorSetHandle) {
            throw new ArgumentException(
                message: "Vulkan descriptor-set handle must be non-zero.",
                paramName: nameof(descriptorSetHandle)
            );
        }

        var bindDescriptorSets = GetPointers(deviceHandle: deviceHandle).CmdBindDescriptorSets;
        var descriptorSetHandles = stackalloc nint[1];

        descriptorSetHandles[0] = descriptorSetHandle;
        bindDescriptorSets(
            commandBufferHandle,
            GraphicsPipelineBindPoint,
            pipelineLayoutHandle,
            0,
            1,
            (nint)descriptorSetHandles,
            0,
            0
        );
    }
    /// <inheritdoc/>
    public void BindDescriptorSets(nint deviceHandle, nint commandBufferHandle, nint pipelineLayoutHandle, nint[] descriptorSetHandles) {
        if (0 == deviceHandle) {
            throw new ArgumentException(
                message: "Vulkan logical-device handle must be non-zero.",
                paramName: nameof(deviceHandle)
            );
        }
        if (0 == commandBufferHandle) {
            throw new ArgumentException(
                message: "Vulkan command-buffer handle must be non-zero.",
                paramName: nameof(commandBufferHandle)
            );
        }
        if (0 == pipelineLayoutHandle) {
            throw new ArgumentException(
                message: "Vulkan pipeline-layout handle must be non-zero.",
                paramName: nameof(pipelineLayoutHandle)
            );
        }
        if (
            (descriptorSetHandles is null) ||
            (0 == descriptorSetHandles.Length)
        ) {
            throw new ArgumentException(
                message: "Descriptor sets must be provided.",
                paramName: nameof(descriptorSetHandles)
            );
        }
        foreach (var descriptorSetHandle in descriptorSetHandles) {
            if (0 == descriptorSetHandle) {
                throw new ArgumentException(
                    message: "Vulkan descriptor-set handles must be non-zero.",
                    paramName: nameof(descriptorSetHandles)
                );
            }
        }

        var bindDescriptorSets = GetPointers(deviceHandle: deviceHandle).CmdBindDescriptorSets;

        fixed (nint* descriptorSetHandlesPointer = descriptorSetHandles) {
            bindDescriptorSets(
                commandBufferHandle,
                GraphicsPipelineBindPoint,
                pipelineLayoutHandle,
                0,
                (uint)descriptorSetHandles.Length,
                (nint)descriptorSetHandlesPointer,
                0,
                0
            );
        }
    }
    /// <inheritdoc/>
    public void BindComputePipeline(nint deviceHandle, nint commandBufferHandle, nint pipelineHandle) {
        var bindPipeline = GetPointers(deviceHandle: deviceHandle).CmdBindPipeline;

        bindPipeline(
            commandBufferHandle,
            ComputePipelineBindPoint,
            pipelineHandle
        );
    }
    /// <inheritdoc/>
    public void BindComputeDescriptorSets(nint deviceHandle, nint commandBufferHandle, nint pipelineLayoutHandle, ReadOnlySpan<nint> descriptorSetHandles) {
        var bindDescriptorSets = GetPointers(deviceHandle: deviceHandle).CmdBindDescriptorSets;

        fixed (nint* descriptorSetHandlesPointer = descriptorSetHandles) {
            bindDescriptorSets(
                commandBufferHandle,
                ComputePipelineBindPoint,
                pipelineLayoutHandle,
                0,
                (uint)descriptorSetHandles.Length,
                (nint)descriptorSetHandlesPointer,
                0,
                0
            );
        }
    }
    /// <inheritdoc/>
    public void Dispatch(nint deviceHandle, nint commandBufferHandle, uint groupCountX, uint groupCountY, uint groupCountZ) {
        var dispatch = GetPointers(deviceHandle: deviceHandle).CmdDispatch;

        dispatch(
            commandBufferHandle,
            groupCountX,
            groupCountY,
            groupCountZ
        );
    }
    /// <inheritdoc/>
    public void DispatchIndirect(nint deviceHandle, nint commandBufferHandle, nint bufferHandle, ulong offset) {
        var dispatchIndirect = GetPointers(deviceHandle: deviceHandle).CmdDispatchIndirect;

        dispatchIndirect(
            commandBufferHandle,
            bufferHandle,
            offset
        );
    }
    /// <inheritdoc/>
    public void Draw(nint deviceHandle, nint commandBufferHandle, uint vertexCount, uint instanceCount, uint firstVertex, uint firstInstance) {
        if (0 == deviceHandle) {
            throw new ArgumentException(
                message: "Vulkan logical-device handle must be non-zero.",
                paramName: nameof(deviceHandle)
            );
        }

        if (0 == commandBufferHandle) {
            throw new ArgumentException(
                message: "Vulkan command-buffer handle must be non-zero.",
                paramName: nameof(commandBufferHandle)
            );
        }

        var cmdDraw = GetPointers(deviceHandle: deviceHandle).CmdDraw;

        cmdDraw(
            commandBufferHandle,
            vertexCount,
            instanceCount,
            firstVertex,
            firstInstance
        );
    }
    /// <inheritdoc/>
    public void TransitionImageLayout(
        nint deviceHandle,
        nint commandBufferHandle,
        nint imageHandle,
        uint baseMipLevel,
        uint mipLevelCount,
        uint oldLayout,
        uint newLayout,
        uint sourceAccessMask,
        uint destinationAccessMask,
        uint sourceStageMask,
        uint destinationStageMask
    ) {
        var pipelineBarrier = GetPointers(deviceHandle: deviceHandle).CmdPipelineBarrier;
        var barrier = new VkImageMemoryBarrier {
            DstAccessMask = destinationAccessMask,
            DstQueueFamilyIndex = QueueFamilyIgnored,
            Image = imageHandle,
            NewLayout = newLayout,
            OldLayout = oldLayout,
            SType = StructureTypeImageMemoryBarrier,
            SrcAccessMask = sourceAccessMask,
            SrcQueueFamilyIndex = QueueFamilyIgnored,
            SubresourceRange = new VkImageSubresourceRange {
                AspectMask = ImageAspectColorBit,
                BaseArrayLayer = 0,
                BaseMipLevel = baseMipLevel,
                LayerCount = 1,
                LevelCount = mipLevelCount,
            },
        };
        var pointer = m_allocator.Alloc(size: Marshal.SizeOf<VkImageMemoryBarrier>());

        try {
            Marshal.StructureToPtr(
                fDeleteOld: false,
                ptr: pointer,
                structure: barrier
            );
            pipelineBarrier(
                commandBufferHandle,
                sourceStageMask,
                destinationStageMask,
                0,
                0,
                0,
                0,
                0,
                1,
                pointer
            );
        } finally {
            m_allocator.Free(ptr: pointer);
        }
    }
    /// <inheritdoc/>
    public void PipelineMemoryBarrier(
        nint deviceHandle,
        nint commandBufferHandle,
        uint sourceAccessMask,
        uint destinationAccessMask,
        uint sourceStageMask,
        uint destinationStageMask
    ) {
        var pipelineBarrier = GetPointers(deviceHandle: deviceHandle).CmdPipelineBarrier;
        // vkCmdPipelineBarrier consumes the host struct synchronously, so it lives on the stack.
        var barrier = new VkMemoryBarrier {
            DstAccessMask = destinationAccessMask,
            SType = StructureTypeMemoryBarrier,
            SrcAccessMask = sourceAccessMask,
        };

        pipelineBarrier(
            commandBufferHandle,
            sourceStageMask,
            destinationStageMask,
            0,
            1,
            (nint)(&barrier),
            0,
            0,
            0,
            0
        );
    }
    /// <inheritdoc/>
    public void ClearColorImage(
        nint deviceHandle,
        nint commandBufferHandle,
        nint imageHandle,
        uint imageLayout,
        float red,
        float green,
        float blue,
        float alpha
    ) {
        var clearColorImage = GetPointers(deviceHandle: deviceHandle).CmdClearColorImage;
        // vkCmdClearColorImage consumes both host structs synchronously, so they live on the stack.
        var clearColor = new VkClearColorValue(
            float32_0: red,
            float32_1: green,
            float32_2: blue,
            float32_3: alpha
        );
        var range = new VkImageSubresourceRange {
            AspectMask = ImageAspectColorBit,
            BaseArrayLayer = 0,
            BaseMipLevel = 0,
            LayerCount = 1,
            LevelCount = 1,
        };

        clearColorImage(
            commandBufferHandle,
            imageHandle,
            imageLayout,
            (nint)(&clearColor),
            1,
            (nint)(&range)
        );
    }
    /// <inheritdoc/>
    public void CopyImageToImage(
        nint deviceHandle,
        nint commandBufferHandle,
        nint sourceImageHandle,
        uint sourceImageLayout,
        nint destinationImageHandle,
        uint destinationImageLayout,
        uint width,
        uint height
    ) {
        var copyImage = GetPointers(deviceHandle: deviceHandle).CmdCopyImage;
        var imageCopy = new VkImageCopy {
            DstOffset = new VkOffset3D(
                x: 0,
                y: 0,
                z: 0
            ),
            DstSubresource = new VkImageSubresourceLayers {
                AspectMask = ImageAspectColorBit,
                BaseArrayLayer = 0,
                LayerCount = 1,
                MipLevel = 0,
            },
            Extent = new VkExtent3D(
                depth: 1,
                height: height,
                width: width
            ),
            SrcOffset = new VkOffset3D(
                x: 0,
                y: 0,
                z: 0
            ),
            SrcSubresource = new VkImageSubresourceLayers {
                AspectMask = ImageAspectColorBit,
                BaseArrayLayer = 0,
                LayerCount = 1,
                MipLevel = 0,
            },
        };
        var copyPointer = m_allocator.Alloc(size: Marshal.SizeOf<VkImageCopy>());

        try {
            Marshal.StructureToPtr(
                fDeleteOld: false,
                ptr: copyPointer,
                structure: imageCopy
            );
            copyImage(
                commandBufferHandle,
                sourceImageHandle,
                sourceImageLayout,
                destinationImageHandle,
                destinationImageLayout,
                1,
                copyPointer
            );
        } finally {
            m_allocator.Free(ptr: copyPointer);
        }
    }
    /// <inheritdoc/>
    public void CopyImageToBuffer(
        nint deviceHandle,
        nint commandBufferHandle,
        nint imageHandle,
        uint imageLayout,
        nint bufferHandle,
        uint width,
        uint height
    ) {
        var copyImageToBuffer = GetPointers(deviceHandle: deviceHandle).CmdCopyImageToBuffer;
        var bufferImageCopy = new VkBufferImageCopy {
            BufferImageHeight = 0,
            BufferOffset = 0,
            BufferRowLength = 0,
            ImageExtent = new VkExtent3D(
                depth: 1,
                height: height,
                width: width
            ),
            ImageOffset = new VkOffset3D(
                x: 0,
                y: 0,
                z: 0
            ),
            ImageSubresource = new VkImageSubresourceLayers {
                AspectMask = ImageAspectColorBit,
                BaseArrayLayer = 0,
                LayerCount = 1,
                MipLevel = 0,
            },
        };
        var pointer = m_allocator.Alloc(size: Marshal.SizeOf<VkBufferImageCopy>());

        try {
            Marshal.StructureToPtr(
                fDeleteOld: false,
                ptr: pointer,
                structure: bufferImageCopy
            );
            copyImageToBuffer(
                commandBufferHandle,
                imageHandle,
                imageLayout,
                bufferHandle,
                1,
                pointer
            );
        } finally {
            m_allocator.Free(ptr: pointer);
        }
    }
    /// <inheritdoc/>
    public void CopyBufferToImage(
        nint deviceHandle,
        nint commandBufferHandle,
        nint bufferHandle,
        nint imageHandle,
        uint imageLayout,
        int imageOffsetX,
        int imageOffsetY,
        uint width,
        uint height
    ) {
        var copyBufferToImage = GetPointers(deviceHandle: deviceHandle).CmdCopyBufferToImage;
        var bufferImageCopy = new VkBufferImageCopy {
            BufferImageHeight = 0,
            BufferOffset = 0,
            BufferRowLength = 0,
            ImageExtent = new VkExtent3D(
                depth: 1,
                height: height,
                width: width
            ),
            ImageOffset = new VkOffset3D(
                x: imageOffsetX,
                y: imageOffsetY,
                z: 0
            ),
            ImageSubresource = new VkImageSubresourceLayers {
                AspectMask = ImageAspectColorBit,
                BaseArrayLayer = 0,
                LayerCount = 1,
                MipLevel = 0,
            },
        };
        var pointer = m_allocator.Alloc(size: Marshal.SizeOf<VkBufferImageCopy>());

        try {
            Marshal.StructureToPtr(
                fDeleteOld: false,
                ptr: pointer,
                structure: bufferImageCopy
            );
            copyBufferToImage(
                commandBufferHandle,
                bufferHandle,
                imageHandle,
                imageLayout,
                1,
                pointer
            );
        } finally {
            m_allocator.Free(ptr: pointer);
        }
    }
    /// <inheritdoc/>
    public void BlitImage(
        nint deviceHandle,
        nint commandBufferHandle,
        nint sourceImageHandle,
        uint sourceImageLayout,
        uint sourceMipLevel,
        uint sourceWidth,
        uint sourceHeight,
        nint destinationImageHandle,
        uint destinationImageLayout,
        uint destinationMipLevel,
        uint destinationWidth,
        uint destinationHeight,
        uint filter
    ) {
        var blitImage = GetPointers(deviceHandle: deviceHandle).CmdBlitImage;
        var blit = new VkImageBlit {
            DstOffset0 = new VkOffset3D(
                x: 0,
                y: 0,
                z: 0
            ),
            DstOffset1 = new VkOffset3D(
                x: (int)destinationWidth,
                y: (int)destinationHeight,
                z: 1
            ),
            DstSubresource = new VkImageSubresourceLayers {
                AspectMask = ImageAspectColorBit,
                BaseArrayLayer = 0,
                LayerCount = 1,
                MipLevel = destinationMipLevel,
            },
            SrcOffset0 = new VkOffset3D(
                x: 0,
                y: 0,
                z: 0
            ),
            SrcOffset1 = new VkOffset3D(
                x: (int)sourceWidth,
                y: (int)sourceHeight,
                z: 1
            ),
            SrcSubresource = new VkImageSubresourceLayers {
                AspectMask = ImageAspectColorBit,
                BaseArrayLayer = 0,
                LayerCount = 1,
                MipLevel = sourceMipLevel,
            },
        };
        var pointer = m_allocator.Alloc(size: Marshal.SizeOf<VkImageBlit>());

        try {
            Marshal.StructureToPtr(
                fDeleteOld: false,
                ptr: pointer,
                structure: blit
            );
            blitImage(
                commandBufferHandle,
                sourceImageHandle,
                sourceImageLayout,
                destinationImageHandle,
                destinationImageLayout,
                1,
                pointer,
                filter
            );
        } finally {
            m_allocator.Free(ptr: pointer);
        }
    }
    /// <inheritdoc/>
    public void PushConstants(
        nint deviceHandle,
        nint commandBufferHandle,
        nint pipelineLayoutHandle,
        uint stageFlags,
        uint offset,
        ReadOnlySpan<byte> data
    ) {
        if (0 == deviceHandle) {
            throw new ArgumentException(
                message: "Vulkan logical-device handle must be non-zero.",
                paramName: nameof(deviceHandle)
            );
        }

        if (0 == commandBufferHandle) {
            throw new ArgumentException(
                message: "Vulkan command-buffer handle must be non-zero.",
                paramName: nameof(commandBufferHandle)
            );
        }

        if (0 == pipelineLayoutHandle) {
            throw new ArgumentException(
                message: "Vulkan pipeline-layout handle must be non-zero.",
                paramName: nameof(pipelineLayoutHandle)
            );
        }

        if (0 == stageFlags) {
            throw new ArgumentOutOfRangeException(
                actualValue: stageFlags,
                message: "Push-constant stage flags must be non-zero.",
                paramName: nameof(stageFlags)
            );
        }

        if (0 == data.Length) {
            throw new ArgumentOutOfRangeException(
                actualValue: data.Length,
                message: "Push-constant data must be non-empty.",
                paramName: nameof(data)
            );
        }

        // vkCmdPushConstants copies the payload during the call, so pinning the span in
        // place suffices — this runs per draw per re-record, where the previous ToArray +
        // pin pair was pure heap churn.
        var cmdPushConstants = GetPointers(deviceHandle: deviceHandle).CmdPushConstants;

        fixed (byte* dataPointer = data) {
            cmdPushConstants(
                commandBufferHandle,
                pipelineLayoutHandle,
                stageFlags,
                offset,
                checked((uint)data.Length),
                (nint)dataPointer
            );
        }
    }
    /// <inheritdoc/>
    public void SetScissor(nint deviceHandle, nint commandBufferHandle, int x, int y, uint width, uint height) {
        if (0 == deviceHandle) {
            throw new ArgumentException(
                message: "Vulkan logical-device handle must be non-zero.",
                paramName: nameof(deviceHandle)
            );
        }

        if (0 == commandBufferHandle) {
            throw new ArgumentException(
                message: "Vulkan command-buffer handle must be non-zero.",
                paramName: nameof(commandBufferHandle)
            );
        }

        if (
            (0 == width) ||
            (0 == height)
        ) {
            throw new ArgumentOutOfRangeException(
                message: "Scissor extents must be greater than zero.",
                paramName: nameof(width)
            );
        }

        var cmdSetScissor = GetPointers(deviceHandle: deviceHandle).CmdSetScissor;
        var scissors = stackalloc VkRect2D[1];

        scissors[0] = new VkRect2D(
            extent: new VkExtent2D(
                height: height,
                width: width
            ),
            offset: new VkOffset2D(
                x: x,
                y: y
            )
        );
        cmdSetScissor(
            commandBufferHandle,
            0,
            1,
            (nint)scissors
        );
    }
    /// <inheritdoc/>
    public void EndRenderPass(nint deviceHandle, nint commandBufferHandle) {
        if (0 == deviceHandle) {
            throw new ArgumentException(
                message: "Vulkan logical-device handle must be non-zero.",
                paramName: nameof(deviceHandle)
            );
        }

        if (0 == commandBufferHandle) {
            throw new ArgumentException(
                message: "Vulkan command-buffer handle must be non-zero.",
                paramName: nameof(commandBufferHandle)
            );
        }

        var endRenderPass = GetPointers(deviceHandle: deviceHandle).CmdEndRenderPass;

        endRenderPass(commandBufferHandle);
    }
    /// <inheritdoc/>
    public VkResult EndCommandBuffer(nint deviceHandle, nint commandBufferHandle) {
        if (0 == deviceHandle) {
            throw new ArgumentException(
                message: "Vulkan logical-device handle must be non-zero.",
                paramName: nameof(deviceHandle)
            );
        }

        if (0 == commandBufferHandle) {
            throw new ArgumentException(
                message: "Vulkan command-buffer handle must be non-zero.",
                paramName: nameof(commandBufferHandle)
            );
        }

        var endCommandBuffer = GetPointers(deviceHandle: deviceHandle).EndCommandBuffer;

        return endCommandBuffer(commandBufferHandle);
    }
    /// <inheritdoc/>
    public void StartRenderPass(VulkanCommandBufferRecordRequest request) {
        ValidateRequest(request: request);

        var startRenderPass = GetPointers(deviceHandle: request.DeviceHandle).CmdBeginRenderPass;
        var clearValue = new VkClearValue {
            Color = new VkClearColorValue(
                float32_0: 0f,
                float32_1: 0f,
                float32_2: 0f,
                float32_3: 1f
            ),
        };
        var clearValuePointer = m_allocator.Alloc(size: Marshal.SizeOf<VkClearValue>());

        try {
            Marshal.StructureToPtr(
                fDeleteOld: false,
                ptr: clearValuePointer,
                structure: clearValue
            );
            var renderArea = new VkRect2D(
                extent: new VkExtent2D(
                    height: request.Height,
                    width: request.Width
                ),
                offset: new VkOffset2D(
                    x: 0,
                    y: 0
                )
            );
            var beginInfo = new VkRenderPassBeginInfo {
                ClearValueCount = 1,
                Framebuffer = request.FramebufferHandle,
                PClearValues = clearValuePointer,
                RenderArea = renderArea,
                RenderPass = request.RenderPassHandle,
                SType = StructureTypeRenderPassBeginInfo,
            };

            startRenderPass(
                request.CommandBufferHandle,
                in beginInfo,
                SubpassContentsInline
            );
        } finally {
            m_allocator.Free(ptr: clearValuePointer);
        }
    }

    private unsafe struct DevicePointers {
        public delegate* unmanaged[Cdecl]<nint, in VkCommandBufferBeginInfo, VkResult> BeginCommandBuffer;
        public delegate* unmanaged[Cdecl]<nint, uint, nint, void> CmdBindPipeline;
        public delegate* unmanaged[Cdecl]<nint, uint, uint, nint, nint, void> CmdBindVertexBuffers;
        public delegate* unmanaged[Cdecl]<nint, uint, nint, uint, uint, nint, uint, nint, void> CmdBindDescriptorSets;
        public delegate* unmanaged[Cdecl]<nint, in VkRenderPassBeginInfo, uint, void> CmdBeginRenderPass;
        public delegate* unmanaged[Cdecl]<nint, uint, uint, uint, uint, void> CmdDraw;
        public delegate* unmanaged[Cdecl]<nint, uint, uint, uint, void> CmdDispatch;
        public delegate* unmanaged[Cdecl]<nint, nint, ulong, void> CmdDispatchIndirect;
        public delegate* unmanaged[Cdecl]<nint, uint, uint, nint, void> CmdSetScissor;
        public delegate* unmanaged[Cdecl]<nint, uint, uint, uint, uint, nint, uint, nint, uint, nint, void> CmdPipelineBarrier;
        public delegate* unmanaged[Cdecl]<nint, nint, uint, nint, uint, nint, void> CmdClearColorImage;
        public delegate* unmanaged[Cdecl]<nint, nint, uint, nint, uint, nint, void> CmdCopyImageToBuffer;
        public delegate* unmanaged[Cdecl]<nint, nint, nint, uint, uint, nint, void> CmdCopyBufferToImage;
        public delegate* unmanaged[Cdecl]<nint, nint, uint, nint, uint, uint, nint, void> CmdCopyImage;
        public delegate* unmanaged[Cdecl]<nint, nint, uint, nint, uint, uint, nint, uint, void> CmdBlitImage;
        public delegate* unmanaged[Cdecl]<nint, nint, uint, uint, uint, nint, void> CmdPushConstants;
        public delegate* unmanaged[Cdecl]<nint, void> CmdEndRenderPass;
        public delegate* unmanaged[Cdecl]<nint, VkResult> EndCommandBuffer;
        // VK_EXT_debug_utils command-buffer labels — null when the extension is not enabled (BeginDebugLabel /
        // EndDebugLabel then no-op).
        public delegate* unmanaged[Cdecl]<nint, in VkDebugUtilsLabelExt, void> CmdBeginDebugUtilsLabel;
        public delegate* unmanaged[Cdecl]<nint, void> CmdEndDebugUtilsLabel;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<nint, DevicePointers> m_pointers = new();

    private unsafe DevicePointers GetPointers(nint deviceHandle) {
        if (m_pointers.TryGetValue(
            key: deviceHandle,
            value: out var pointers
        )) {
            return pointers;
        }
        var getAddr = GetDeviceProcAddr();
        DevicePointers pNew = default;

        fixed (byte* pName = "vkBeginCommandBuffer"u8) {
            pNew.BeginCommandBuffer = (delegate* unmanaged[Cdecl]<nint, in VkCommandBufferBeginInfo, VkResult>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkCmdBindPipeline"u8) {
            pNew.CmdBindPipeline = (delegate* unmanaged[Cdecl]<nint, uint, nint, void>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkCmdBindVertexBuffers"u8) {
            pNew.CmdBindVertexBuffers = (delegate* unmanaged[Cdecl]<nint, uint, uint, nint, nint, void>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkCmdBindDescriptorSets"u8) {
            pNew.CmdBindDescriptorSets = (delegate* unmanaged[Cdecl]<nint, uint, nint, uint, uint, nint, uint, nint, void>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkCmdBeginRenderPass"u8) {
            pNew.CmdBeginRenderPass = (delegate* unmanaged[Cdecl]<nint, in VkRenderPassBeginInfo, uint, void>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkCmdDraw"u8) {
            pNew.CmdDraw = (delegate* unmanaged[Cdecl]<nint, uint, uint, uint, uint, void>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkCmdSetScissor"u8) {
            pNew.CmdSetScissor = (delegate* unmanaged[Cdecl]<nint, uint, uint, nint, void>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkCmdPipelineBarrier"u8) {
            pNew.CmdPipelineBarrier = (delegate* unmanaged[Cdecl]<nint, uint, uint, uint, uint, nint, uint, nint, uint, nint, void>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkCmdClearColorImage"u8) {
            pNew.CmdClearColorImage = (delegate* unmanaged[Cdecl]<nint, nint, uint, nint, uint, nint, void>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkCmdCopyImageToBuffer"u8) {
            pNew.CmdCopyImageToBuffer = (delegate* unmanaged[Cdecl]<nint, nint, uint, nint, uint, nint, void>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkCmdCopyBufferToImage"u8) {
            pNew.CmdCopyBufferToImage = (delegate* unmanaged[Cdecl]<nint, nint, nint, uint, uint, nint, void>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkCmdCopyImage"u8) {
            pNew.CmdCopyImage = (delegate* unmanaged[Cdecl]<nint, nint, uint, nint, uint, uint, nint, void>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkCmdBlitImage"u8) {
            pNew.CmdBlitImage = (delegate* unmanaged[Cdecl]<nint, nint, uint, nint, uint, uint, nint, uint, void>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkCmdDispatch"u8) {
            pNew.CmdDispatch = (delegate* unmanaged[Cdecl]<nint, uint, uint, uint, void>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkCmdDispatchIndirect"u8) {
            pNew.CmdDispatchIndirect = (delegate* unmanaged[Cdecl]<nint, nint, ulong, void>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkCmdPushConstants"u8) {
            pNew.CmdPushConstants = (delegate* unmanaged[Cdecl]<nint, nint, uint, uint, uint, nint, void>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkCmdEndRenderPass"u8) {
            pNew.CmdEndRenderPass = (delegate* unmanaged[Cdecl]<nint, void>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkEndCommandBuffer"u8) {
            pNew.EndCommandBuffer = (delegate* unmanaged[Cdecl]<nint, VkResult>)getAddr(
                deviceHandle,
                pName
            );
        }
        // Optional (VK_EXT_debug_utils): getAddr returns null when the extension is not enabled, leaving the label
        // methods as no-ops. The command-buffer label commands are device-child, so vkGetDeviceProcAddr resolves them
        // once the instance extension is on.
        fixed (byte* pName = "vkCmdBeginDebugUtilsLabelEXT"u8) {
            pNew.CmdBeginDebugUtilsLabel = (delegate* unmanaged[Cdecl]<nint, in VkDebugUtilsLabelExt, void>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkCmdEndDebugUtilsLabelEXT"u8) {
            pNew.CmdEndDebugUtilsLabel = (delegate* unmanaged[Cdecl]<nint, void>)getAddr(
                deviceHandle,
                pName
            );
        }
        m_pointers[deviceHandle] = pNew;
        return pNew;
    }
    private unsafe delegate* unmanaged[Cdecl]<nint, byte*, nint> GetDeviceProcAddr() {
        lock (m_syncRoot) {
            if (m_getDeviceProcAddr is not null) {
                return m_getDeviceProcAddr;
            }
            var export = VulkanNativeLibrary.GetExport(functionName: "vkGetDeviceProcAddr");

            m_getDeviceProcAddr = (delegate* unmanaged[Cdecl]<nint, byte*, nint>)export;
            return m_getDeviceProcAddr;
        }
    }
    private static unsafe void ValidateRequest(VulkanCommandBufferRecordRequest request) {
        if (0 == request.DeviceHandle) {
            throw new ArgumentException(
                message: "Vulkan logical-device handle must be non-zero.",
                paramName: nameof(request)
            );
        }

        if (0 == request.CommandBufferHandle) {
            throw new ArgumentException(
                message: "Vulkan command-buffer handle must be non-zero.",
                paramName: nameof(request)
            );
        }

        if (0 == request.FramebufferHandle) {
            throw new ArgumentException(
                message: "Vulkan framebuffer handle must be non-zero.",
                paramName: nameof(request)
            );
        }

        if (0 == request.RenderPassHandle) {
            throw new ArgumentException(
                message: "Vulkan render-pass handle must be non-zero.",
                paramName: nameof(request)
            );
        }
    }
}
