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
    private const uint ComputePipelineBindPoint = 1;
    private const uint GraphicsPipelineBindPoint = 0;
    private const uint ImageAspectColorBit = 0x00000001;
    private const uint QueueFamilyIgnored = 0xFFFFFFFF;
    private const uint StructureTypeBufferMemoryBarrier = 44;
    private const uint StructureTypeCommandBufferBeginInfo = 42;
    private const uint StructureTypeDebugUtilsLabel = 1000128002;
    private const uint StructureTypeImageMemoryBarrier = 45;
    private const uint StructureTypeMemoryBarrier = 46;
    private const uint StructureTypeRenderPassBeginInfo = 43;
    private const uint SubpassContentsInline = 0;
    private const ulong WholeSize = ulong.MaxValue;

    // The clear value of a render pass whose request names none: its one color attachment, opaque black.
    private static readonly VkClearValue[] OpaqueBlack = [VkClearValue.OfColor(alpha: 1f, blue: 0f, green: 0f, red: 0f)];

    private readonly IAllocator m_allocator;

    /// <summary>Initializes a new instance of the <see cref="VulkanNativeCommandBufferRecordingApi"/> class.</summary>
    /// <param name="allocator">The unmanaged allocator used to marshal native Vulkan structures.</param>
    /// <exception cref="ArgumentNullException"><paramref name="allocator"/> is <see langword="null"/>.</exception>
    public VulkanNativeCommandBufferRecordingApi(IAllocator allocator) {
        ArgumentNullException.ThrowIfNull(argument: allocator);

        m_allocator = allocator;
    }

    private static unsafe void ValidateRequest(VulkanCommandBufferRecordRequest request) {
        ArgumentNullException.ThrowIfNull(
            argument: request.Device,
            paramName: nameof(request)
        );

        VulkanArgument.RequireHandle(
            handle: request.CommandBufferHandle,
            handleDescription: "command-buffer",
            paramName: nameof(request)
        );

        VulkanArgument.RequireHandle(
            handle: request.FramebufferHandle,
            handleDescription: "framebuffer",
            paramName: nameof(request)
        );

        VulkanArgument.RequireHandle(
            handle: request.RenderPassHandle,
            handleDescription: "render-pass",
            paramName: nameof(request)
        );
    }

    /// <inheritdoc/>
    public VkResult BeginCommandBuffer(VulkanCommandBufferRecordRequest request) {
        ValidateRequest(request: request);

        var beginCommandBuffer = request.Device.BeginCommandBuffer;
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
    public VkResult BeginCommandBuffer(VulkanDeviceCommands device, nint commandBufferHandle) {
        ArgumentNullException.ThrowIfNull(argument: device);

        VulkanArgument.RequireHandle(
            handle: commandBufferHandle,
            handleDescription: "command-buffer",
            paramName: nameof(commandBufferHandle)
        );

        var beginCommandBuffer = device.BeginCommandBuffer;
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
    public void BeginDebugLabel(VulkanDeviceCommands device, nint commandBufferHandle, string label) {
        ArgumentException.ThrowIfNullOrEmpty(argument: label);

        var beginLabel = device.CmdBeginDebugUtilsLabelExt;

        // VK_EXT_debug_utils absent (extension not enabled, or no capture layer resolving the entry point): no-op.
        if (beginLabel is null) {
            return;
        }

        var byteCount = Encoding.UTF8.GetByteCount(s: label);
        Span<byte> nameBytes = stackalloc byte[(byteCount + 1)];

        Encoding.UTF8.GetBytes(
            bytes: nameBytes,
            chars: label
        );
        nameBytes[byteCount] = 0;

        fixed (byte* labelName = nameBytes) {
            var info = new VkDebugUtilsLabelExt {
                LabelName = labelName,
                StructureType = StructureTypeDebugUtilsLabel,
            };

            beginLabel(
                commandBufferHandle,
                in info
            );
        }
    }
    /// <inheritdoc/>
    public void BindComputeDescriptorSets(VulkanDeviceCommands device, nint commandBufferHandle, nint pipelineLayoutHandle, ReadOnlySpan<nint> descriptorSetHandles) {
        var bindDescriptorSets = device.CmdBindDescriptorSets;

        fixed (nint* descriptorSetHandlesPointer = descriptorSetHandles) {
            bindDescriptorSets(
                commandBufferHandle,
                ComputePipelineBindPoint,
                pipelineLayoutHandle,
                0,
                ((uint)descriptorSetHandles.Length),
                ((nint)descriptorSetHandlesPointer),
                0,
                0
            );
        }
    }
    /// <inheritdoc/>
    public void BindComputePipeline(VulkanDeviceCommands device, nint commandBufferHandle, nint pipelineHandle) {
        var bindPipeline = device.CmdBindPipeline;

        bindPipeline(
            commandBufferHandle,
            ComputePipelineBindPoint,
            pipelineHandle
        );
    }
    /// <inheritdoc/>
    public void BindDescriptorSet(VulkanDeviceCommands device, nint commandBufferHandle, nint pipelineLayoutHandle, nint descriptorSetHandle) {
        ArgumentNullException.ThrowIfNull(argument: device);

        VulkanArgument.RequireHandle(
            handle: commandBufferHandle,
            handleDescription: "command-buffer",
            paramName: nameof(commandBufferHandle)
        );

        VulkanArgument.RequireHandle(
            handle: pipelineLayoutHandle,
            handleDescription: "pipeline-layout",
            paramName: nameof(pipelineLayoutHandle)
        );

        VulkanArgument.RequireHandle(
            handle: descriptorSetHandle,
            handleDescription: "descriptor-set",
            paramName: nameof(descriptorSetHandle)
        );

        var bindDescriptorSets = device.CmdBindDescriptorSets;
        var descriptorSetHandles = stackalloc nint[1];

        descriptorSetHandles[0] = descriptorSetHandle;
        bindDescriptorSets(
            commandBufferHandle,
            GraphicsPipelineBindPoint,
            pipelineLayoutHandle,
            0,
            1,
            ((nint)descriptorSetHandles),
            0,
            0
        );
    }
    /// <inheritdoc/>
    public void BindDescriptorSets(VulkanDeviceCommands device, nint commandBufferHandle, nint pipelineLayoutHandle, nint[] descriptorSetHandles) {
        ArgumentNullException.ThrowIfNull(argument: device);
        VulkanArgument.RequireHandle(
            handle: commandBufferHandle,
            handleDescription: "command-buffer",
            paramName: nameof(commandBufferHandle)
        );
        VulkanArgument.RequireHandle(
            handle: pipelineLayoutHandle,
            handleDescription: "pipeline-layout",
            paramName: nameof(pipelineLayoutHandle)
        );
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

        var bindDescriptorSets = device.CmdBindDescriptorSets;

        fixed (nint* descriptorSetHandlesPointer = descriptorSetHandles) {
            bindDescriptorSets(
                commandBufferHandle,
                GraphicsPipelineBindPoint,
                pipelineLayoutHandle,
                0,
                ((uint)descriptorSetHandles.Length),
                ((nint)descriptorSetHandlesPointer),
                0,
                0
            );
        }
    }
    /// <inheritdoc/>
    public void BindGraphicsPipeline(VulkanDeviceCommands device, nint commandBufferHandle, nint pipelineHandle) {
        ArgumentNullException.ThrowIfNull(argument: device);

        VulkanArgument.RequireHandle(
            handle: commandBufferHandle,
            handleDescription: "command-buffer",
            paramName: nameof(commandBufferHandle)
        );

        VulkanArgument.RequireHandle(
            handle: pipelineHandle,
            handleDescription: "graphics-pipeline",
            paramName: nameof(pipelineHandle)
        );

        var bindPipeline = device.CmdBindPipeline;

        bindPipeline(
            commandBufferHandle,
            GraphicsPipelineBindPoint,
            pipelineHandle
        );
    }
    /// <inheritdoc/>
    public void BindVertexBuffer(VulkanDeviceCommands device, nint commandBufferHandle, VulkanVertexBufferBinding vertexBufferBinding) {
        ArgumentNullException.ThrowIfNull(argument: device);

        VulkanArgument.RequireHandle(
            handle: commandBufferHandle,
            handleDescription: "command-buffer",
            paramName: nameof(commandBufferHandle)
        );

        // vkCmdBindVertexBuffers copies both arrays during the call, so stack storage
        // suffices (same pattern as BindDescriptorSet) — this runs per draw per
        // re-record, where the previous array + pin pair was pure heap churn.
        var bindVertexBuffers = device.CmdBindVertexBuffers;
        var bufferHandles = stackalloc nint[1];

        bufferHandles[0] = vertexBufferBinding.BufferHandle;
        var offsets = stackalloc ulong[1];

        offsets[0] = vertexBufferBinding.Offset;
        bindVertexBuffers(
            commandBufferHandle,
            0,
            1,
            ((nint)bufferHandles),
            ((nint)offsets)
        );
    }
    /// <inheritdoc/>
    public void BlitImage(
        VulkanDeviceCommands device,
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
        var blitImage = device.CmdBlitImage;
        var blit = new VkImageBlit {
            DstOffset0 = new VkOffset3D(
            x: 0,
            y: 0,
            z: 0
        ),
            DstOffset1 = new VkOffset3D(
            x: ((int)destinationWidth),
            y: ((int)destinationHeight),
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
            x: ((int)sourceWidth),
            y: ((int)sourceHeight),
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
    public void ClearColorImage(
        VulkanDeviceCommands device,
        nint commandBufferHandle,
        nint imageHandle,
        uint imageLayout,
        float red,
        float green,
        float blue,
        float alpha
    ) {
        var clearColorImage = device.CmdClearColorImage;
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
            ((nint)(&clearColor)),
            1,
            ((nint)(&range))
        );
    }
    /// <inheritdoc/>
    public void CopyBufferToImage(
        VulkanDeviceCommands device,
        nint commandBufferHandle,
        nint bufferHandle,
        nint imageHandle,
        uint imageLayout,
        int imageOffsetX,
        int imageOffsetY,
        uint width,
        uint height
    ) {
        var copyBufferToImage = device.CmdCopyBufferToImage;
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
    public void CopyImageToBuffer(
        VulkanDeviceCommands device,
        nint commandBufferHandle,
        nint imageHandle,
        uint imageLayout,
        nint bufferHandle,
        uint width,
        uint height
    ) {
        var copyImageToBuffer = device.CmdCopyImageToBuffer;
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
    public void CopyImageToImage(
        VulkanDeviceCommands device,
        nint commandBufferHandle,
        nint sourceImageHandle,
        uint sourceImageLayout,
        nint destinationImageHandle,
        uint destinationImageLayout,
        uint width,
        uint height
    ) {
        var copyImage = device.CmdCopyImage;
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
    public void Dispatch(VulkanDeviceCommands device, nint commandBufferHandle, uint groupCountX, uint groupCountY, uint groupCountZ) {
        var dispatch = device.CmdDispatch;

        dispatch(
            commandBufferHandle,
            groupCountX,
            groupCountY,
            groupCountZ
        );
    }
    /// <inheritdoc/>
    public void DispatchIndirect(VulkanDeviceCommands device, nint commandBufferHandle, nint bufferHandle, ulong offset) {
        var dispatchIndirect = device.CmdDispatchIndirect;

        dispatchIndirect(
            commandBufferHandle,
            bufferHandle,
            offset
        );
    }
    /// <inheritdoc/>
    public void BindIndexBuffer(VulkanDeviceCommands device, nint commandBufferHandle, nint bufferHandle, ulong offsetBytes, uint indexType) {
        ArgumentNullException.ThrowIfNull(argument: device);

        VulkanArgument.RequireHandle(
            handle: commandBufferHandle,
            handleDescription: "command-buffer",
            paramName: nameof(commandBufferHandle)
        );

        var bindIndexBuffer = device.CmdBindIndexBuffer;

        bindIndexBuffer(
            commandBufferHandle,
            bufferHandle,
            offsetBytes,
            indexType
        );
    }
    /// <inheritdoc/>
    public void DrawIndexed(VulkanDeviceCommands device, nint commandBufferHandle, uint indexCount, uint instanceCount, uint firstIndex, int vertexOffset, uint firstInstance) {
        ArgumentNullException.ThrowIfNull(argument: device);

        VulkanArgument.RequireHandle(
            handle: commandBufferHandle,
            handleDescription: "command-buffer",
            paramName: nameof(commandBufferHandle)
        );

        var drawIndexed = device.CmdDrawIndexed;

        drawIndexed(
            commandBufferHandle,
            indexCount,
            instanceCount,
            firstIndex,
            vertexOffset,
            firstInstance
        );
    }
    /// <inheritdoc/>
    public void Draw(VulkanDeviceCommands device, nint commandBufferHandle, uint vertexCount, uint instanceCount, uint firstVertex, uint firstInstance) {
        ArgumentNullException.ThrowIfNull(argument: device);

        VulkanArgument.RequireHandle(
            handle: commandBufferHandle,
            handleDescription: "command-buffer",
            paramName: nameof(commandBufferHandle)
        );

        var cmdDraw = device.CmdDraw;

        cmdDraw(
            commandBufferHandle,
            vertexCount,
            instanceCount,
            firstVertex,
            firstInstance
        );
    }
    /// <inheritdoc/>
    public VkResult EndCommandBuffer(VulkanDeviceCommands device, nint commandBufferHandle) {
        ArgumentNullException.ThrowIfNull(argument: device);

        VulkanArgument.RequireHandle(
            handle: commandBufferHandle,
            handleDescription: "command-buffer",
            paramName: nameof(commandBufferHandle)
        );

        var endCommandBuffer = device.EndCommandBuffer;

        return endCommandBuffer(commandBufferHandle);
    }
    /// <inheritdoc/>
    public void EndDebugLabel(VulkanDeviceCommands device, nint commandBufferHandle) {
        var endLabel = device.CmdEndDebugUtilsLabelExt;

        // VK_EXT_debug_utils absent: no-op (balances a BeginDebugLabel that also no-oped).
        if (endLabel is not null) {
            endLabel(commandBufferHandle);
        }
    }
    /// <inheritdoc/>
    public void EndRenderPass(VulkanDeviceCommands device, nint commandBufferHandle) {
        ArgumentNullException.ThrowIfNull(argument: device);

        VulkanArgument.RequireHandle(
            handle: commandBufferHandle,
            handleDescription: "command-buffer",
            paramName: nameof(commandBufferHandle)
        );

        var endRenderPass = device.CmdEndRenderPass;

        endRenderPass(commandBufferHandle);
    }
    /// <inheritdoc/>
    public void FillBuffer(VulkanDeviceCommands device, nint commandBufferHandle, nint bufferHandle, ulong sizeBytes) {
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
        var fillBuffer = device.CmdFillBuffer;

        fillBuffer(
            commandBufferHandle,
            bufferHandle,
            0,
            sizeBytes,
            0
        );
    }
    /// <inheritdoc/>
    public void PipelineBufferBarrier(
        VulkanDeviceCommands device,
        nint commandBufferHandle,
        nint bufferHandle,
        uint sourceAccessMask,
        uint destinationAccessMask,
        uint sourceStageMask,
        uint destinationStageMask
    ) {
        var pipelineBarrier = device.CmdPipelineBarrier;
        // vkCmdPipelineBarrier consumes the host struct synchronously, so it lives on the stack.
        var barrier = new VkBufferMemoryBarrier {
            Buffer = bufferHandle,
            DstAccessMask = destinationAccessMask,
            DstQueueFamilyIndex = QueueFamilyIgnored,
            Offset = 0,
            SType = StructureTypeBufferMemoryBarrier,
            Size = WholeSize,
            SrcAccessMask = sourceAccessMask,
            SrcQueueFamilyIndex = QueueFamilyIgnored,
        };

        pipelineBarrier(
            commandBufferHandle,
            sourceStageMask,
            destinationStageMask,
            0,
            0,
            0,
            1,
            ((nint)(&barrier)),
            0,
            0
        );
    }
    /// <inheritdoc/>
    public void PipelineMemoryBarrier(
        VulkanDeviceCommands device,
        nint commandBufferHandle,
        uint sourceAccessMask,
        uint destinationAccessMask,
        uint sourceStageMask,
        uint destinationStageMask
    ) {
        var pipelineBarrier = device.CmdPipelineBarrier;
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
            ((nint)(&barrier)),
            0,
            0,
            0,
            0
        );
    }
    /// <inheritdoc/>
    public void PushConstants(
        VulkanDeviceCommands device,
        nint commandBufferHandle,
        nint pipelineLayoutHandle,
        uint stageFlags,
        uint offset,
        ReadOnlySpan<byte> data
    ) {
        ArgumentNullException.ThrowIfNull(argument: device);

        VulkanArgument.RequireHandle(
            handle: commandBufferHandle,
            handleDescription: "command-buffer",
            paramName: nameof(commandBufferHandle)
        );

        VulkanArgument.RequireHandle(
            handle: pipelineLayoutHandle,
            handleDescription: "pipeline-layout",
            paramName: nameof(pipelineLayoutHandle)
        );

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
        var cmdPushConstants = device.CmdPushConstants;

        fixed (byte* dataPointer = data) {
            cmdPushConstants(
                commandBufferHandle,
                pipelineLayoutHandle,
                stageFlags,
                offset,
                checked((uint)data.Length),
                ((nint)dataPointer)
            );
        }
    }
    /// <inheritdoc/>
    public void SetScissor(VulkanDeviceCommands device, nint commandBufferHandle, int x, int y, uint width, uint height) {
        ArgumentNullException.ThrowIfNull(argument: device);

        VulkanArgument.RequireHandle(
            handle: commandBufferHandle,
            handleDescription: "command-buffer",
            paramName: nameof(commandBufferHandle)
        );

        if (
            (0 == width) ||
            (0 == height)
        ) {
            throw new ArgumentOutOfRangeException(
                message: "Scissor extents must be greater than zero.",
                paramName: nameof(width)
            );
        }

        var cmdSetScissor = device.CmdSetScissor;
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
            ((nint)scissors)
        );
    }
    /// <inheritdoc/>
    public void SetViewport(VulkanDeviceCommands device, nint commandBufferHandle, in VkViewport viewport) {
        ArgumentNullException.ThrowIfNull(argument: device);

        VulkanArgument.RequireHandle(
            handle: commandBufferHandle,
            handleDescription: "command-buffer",
            paramName: nameof(commandBufferHandle)
        );

        var copy = viewport;

        device.CmdSetViewport(
            commandBufferHandle,
            0,
            1,
            ((nint)(&copy))
        );
    }
    /// <inheritdoc/>
    public void StartRenderPass(VulkanCommandBufferRecordRequest request) {
        ValidateRequest(request: request);

        var startRenderPass = request.Device.CmdBeginRenderPass;
        var clearValues = (request.ClearValues ?? OpaqueBlack);
        var clearValuePointer = VulkanMarshalHelpers.AllocateArray(
            allocator: m_allocator,
            values: clearValues
        );

        try {
            var renderArea = new VkRect2D(
                extent: new VkExtent2D(
                    height: request.Height,
                    width: request.Width
                ),
                offset: new VkOffset2D(
                    x: request.X,
                    y: request.Y
                )
            );
            var beginInfo = new VkRenderPassBeginInfo {
                ClearValueCount = ((uint)clearValues.Count),
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
    /// <inheritdoc/>
    public void TransitionImageLayout(
        VulkanDeviceCommands device,
        nint commandBufferHandle,
        nint imageHandle,
        uint aspectMask,
        uint baseMipLevel,
        uint mipLevelCount,
        uint oldLayout,
        uint newLayout,
        uint sourceAccessMask,
        uint destinationAccessMask,
        uint sourceStageMask,
        uint destinationStageMask
    ) {
        var pipelineBarrier = device.CmdPipelineBarrier;
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
                AspectMask = aspectMask,
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
}
