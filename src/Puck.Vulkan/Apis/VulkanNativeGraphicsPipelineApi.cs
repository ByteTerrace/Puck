using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan;

/// <summary>
/// The native implementation of <see cref="IVulkanGraphicsPipelineApi"/>, marshaling to the graphics-pipeline entry
/// point resolved from the Vulkan loader, with its layouts made by <see cref="VulkanPipelineLayouts"/>.
/// </summary>
public unsafe sealed class VulkanNativeGraphicsPipelineApi : IVulkanGraphicsPipelineApi {
    private const uint False = 0;
    private const uint LogicOpCopy = 3;
    private const uint ShaderStageFragmentBit = 0x00000010;
    private const uint ShaderStageVertexBit = 0x00000001;
    private const uint StructureTypeGraphicsPipelineCreateInfo = 28;
    private const uint StructureTypePipelineColorBlendStateCreateInfo = 26;
    private const uint StructureTypePipelineDynamicStateCreateInfo = 27;
    private const uint StructureTypePipelineInputAssemblyStateCreateInfo = 20;
    private const uint StructureTypePipelineShaderStageCreateInfo = 18;
    private const uint StructureTypePipelineVertexInputStateCreateInfo = 19;
    private const uint StructureTypePipelineViewportStateCreateInfo = 22;

    private readonly IAllocator m_allocator;

    /// <summary>Initializes a new instance of the <see cref="VulkanNativeGraphicsPipelineApi"/> class.</summary>
    /// <param name="allocator">The unmanaged allocator used to marshal native Vulkan structures.</param>
    /// <exception cref="ArgumentNullException"><paramref name="allocator"/> is <see langword="null"/>.</exception>
    public VulkanNativeGraphicsPipelineApi(IAllocator allocator) {
        ArgumentNullException.ThrowIfNull(argument: allocator);

        m_allocator = allocator;
    }

    private static void ValidateRequest(VulkanGraphicsPipelineCreateRequest request) {
        ArgumentNullException.ThrowIfNull(
            argument: request.Device,
            paramName: nameof(request)
        );

        VulkanArgument.RequireHandle(
            handle: request.RenderPassHandle,
            handleDescription: "render-pass",
            paramName: nameof(request)
        );

        VulkanArgument.RequireHandle(
            handle: request.VertexShaderModuleHandle,
            handleDescription: "vertex shader-module",
            paramName: nameof(request)
        );

        VulkanArgument.RequireHandle(
            handle: request.FragmentShaderModuleHandle,
            handleDescription: "fragment shader-module",
            paramName: nameof(request)
        );

        if (
            (request.FixedViewport is { } extent) &&
            ((0 == extent.Width) || (0 == extent.Height))
        ) {
            throw new ArgumentOutOfRangeException(
                message: "A fixed Vulkan graphics-pipeline viewport must be larger than zero in both directions.",
                paramName: nameof(request)
            );
        }
    }

    /// <inheritdoc/>
    public VkResult CreateGraphicsPipeline(
        VulkanGraphicsPipelineCreateRequest request,
        out nint descriptorSetLayoutHandle,
        out nint pipelineLayoutHandle,
        out nint pipelineHandle
    ) {
        ValidateRequest(request: request);

        pipelineHandle = 0;

        // A layout the caller created stays the caller's, so a failed creation destroys only what it made here.
        var ownsLayout = (0 == request.PipelineLayoutHandle);

        if (ownsLayout) {
            var layoutResult = VulkanPipelineLayouts.Create(
                allocator: m_allocator,
                bindings: request.DescriptorBindings,
                descriptorSetLayoutHandle: out descriptorSetLayoutHandle,
                device: request.Device,
                pipelineLayoutHandle: out pipelineLayoutHandle,
                pushConstantSize: request.PushConstantSize,
                pushConstantStageFlags: request.PushConstantStageFlags
            );

            if (!layoutResult.IsSuccess()) {
                return layoutResult;
            }
        } else {
            descriptorSetLayoutHandle = 0;
            pipelineLayoutHandle = request.PipelineLayoutHandle;
        }

        var vertexBindings = (request.VertexBindings ?? []);
        var vertexAttributes = (request.VertexAttributes ?? []);
        var dynamicStates = (request.DynamicStates ?? []);
        var colorBlendAttachments = (request.ColorBlendAttachments ?? []);
        var result = VkResult.ErrorInitializationFailed;
        nint vertexBindingsPointer = 0;
        nint vertexAttributesPointer = 0;
        nint dynamicStatesPointer = 0;
        nint colorBlendAttachmentsPointer = 0;

        try {
            vertexBindingsPointer = VulkanMarshalHelpers.AllocateArray(
                allocator: m_allocator,
                values: vertexBindings
            );
            vertexAttributesPointer = VulkanMarshalHelpers.AllocateArray(
                allocator: m_allocator,
                values: vertexAttributes
            );
            dynamicStatesPointer = VulkanMarshalHelpers.AllocateArray(
                allocator: m_allocator,
                values: dynamicStates
            );
            colorBlendAttachmentsPointer = VulkanMarshalHelpers.AllocateArray(
                allocator: m_allocator,
                values: colorBlendAttachments
            );

            // Every state structure is a stack local: vkCreateGraphicsPipelines reads the whole graph synchronously,
            // so their addresses stay valid for the call.
            var stages = stackalloc VkPipelineShaderStageCreateInfo[2];

            stages[0] = new VkPipelineShaderStageCreateInfo {
                Module = request.VertexShaderModuleHandle,
                PName = VulkanMarshalHelpers.MainEntryPoint,
                SType = StructureTypePipelineShaderStageCreateInfo,
                Stage = ShaderStageVertexBit,
            };
            stages[1] = new VkPipelineShaderStageCreateInfo {
                Module = request.FragmentShaderModuleHandle,
                PName = VulkanMarshalHelpers.MainEntryPoint,
                SType = StructureTypePipelineShaderStageCreateInfo,
                Stage = ShaderStageFragmentBit,
            };

            var vertexInputState = new VkPipelineVertexInputStateCreateInfo {
                PVertexAttributeDescriptions = vertexAttributesPointer,
                PVertexBindingDescriptions = vertexBindingsPointer,
                SType = StructureTypePipelineVertexInputStateCreateInfo,
                VertexAttributeDescriptionCount = ((uint)vertexAttributes.Count),
                VertexBindingDescriptionCount = ((uint)vertexBindings.Count),
            };
            var inputAssemblyState = new VkPipelineInputAssemblyStateCreateInfo {
                PrimitiveRestartEnable = False,
                SType = StructureTypePipelineInputAssemblyStateCreateInfo,
                Topology = request.Topology,
            };
            // One viewport and one scissor. A fixed pair covers the request's extent from the origin; a dynamic pair
            // leaves both pointers null, which the dynamic state makes legal.
            var fixedExtent = request.FixedViewport.GetValueOrDefault();
            var viewport = new VkViewport(
                height: fixedExtent.Height,
                maxDepth: 1f,
                minDepth: 0f,
                width: fixedExtent.Width,
                x: 0f,
                y: 0f
            );
            var scissor = new VkRect2D(
                extent: fixedExtent,
                offset: new VkOffset2D(
                    x: 0,
                    y: 0
                )
            );
            var viewportState = new VkPipelineViewportStateCreateInfo {
                PScissors = ((request.FixedViewport is null)
                    ? 0
                    : ((nint)(&scissor))),
                PViewports = ((request.FixedViewport is null)
                    ? 0
                    : ((nint)(&viewport))),
                SType = StructureTypePipelineViewportStateCreateInfo,
                ScissorCount = 1,
                ViewportCount = 1,
            }; var dynamicState = new VkPipelineDynamicStateCreateInfo {
                DynamicStateCount = ((uint)dynamicStates.Count),
                PDynamicStates = dynamicStatesPointer,
                SType = StructureTypePipelineDynamicStateCreateInfo,
            };
            var rasterizationState = request.Rasterization;
            var multisampleState = request.Multisample;
            var depthStencilState = request.DepthStencil.GetValueOrDefault();
            var colorBlendState = new VkPipelineColorBlendStateCreateInfo {
                AttachmentCount = ((uint)colorBlendAttachments.Count),
                LogicOp = LogicOpCopy,
                LogicOpEnable = False,
                PAttachments = colorBlendAttachmentsPointer,
                SType = StructureTypePipelineColorBlendStateCreateInfo,
            };
            // Creation feedback (core in Vulkan 1.3) says whether the pipeline cache answered.
            var feedback = default(VkPipelineCreationFeedback);
            var stageFeedback = stackalloc VkPipelineCreationFeedback[2];
            var feedbackInfo = new VkPipelineCreationFeedbackCreateInfo {
                PPipelineCreationFeedback = ((nint)(&feedback)),
                PPipelineStageCreationFeedbacks = ((nint)stageFeedback),
                PipelineStageCreationFeedbackCount = 2,
                SType = VkPipelineCreationFeedbackCreateInfo.StructureType,
            };
            var pipelineCreateInfo = new VkGraphicsPipelineCreateInfo {
                BasePipelineHandle = 0,
                BasePipelineIndex = -1,
                Layout = pipelineLayoutHandle,
                PColorBlendState = ((nint)(&colorBlendState)),
                PDepthStencilState = (request.DepthStencil.HasValue
                    ? ((nint)(&depthStencilState))
                    : 0),
                PDynamicState = ((nint)(&dynamicState)),
                PInputAssemblyState = ((nint)(&inputAssemblyState)),
                PMultisampleState = ((nint)(&multisampleState)),
                PNext = ((nint)(&feedbackInfo)),
                PRasterizationState = ((nint)(&rasterizationState)),
                PStages = ((nint)stages),
                PVertexInputState = ((nint)(&vertexInputState)),
                PViewportState = ((nint)(&viewportState)),
                RenderPass = request.RenderPassHandle,
                SType = StructureTypeGraphicsPipelineCreateInfo,
                StageCount = 2,
                Subpass = 0,
            };

            result = request.Device.CreateGraphicsPipelines(
                request.Device.Handle,
                (request.PipelineCache?.Handle ?? 0),
                1,
                ((nint)(&pipelineCreateInfo)),
                0,
                out pipelineHandle
            );

            if (result.IsSuccess()) {
                request.PipelineCache?.Count(feedback: in feedback);
            }

            return result;
        } finally {
            m_allocator.Free(ptr: vertexBindingsPointer);
            m_allocator.Free(ptr: vertexAttributesPointer);
            m_allocator.Free(ptr: dynamicStatesPointer);
            m_allocator.Free(ptr: colorBlendAttachmentsPointer);

            if (!result.IsSuccess()) {
                if (ownsLayout) {
                    VulkanPipelineLayouts.Destroy(
                        descriptorSetLayoutHandle: descriptorSetLayoutHandle,
                        device: request.Device,
                        pipelineLayoutHandle: pipelineLayoutHandle
                    );
                }

                descriptorSetLayoutHandle = 0;
                pipelineLayoutHandle = 0;
                pipelineHandle = 0;
            }
        }
    }
    /// <inheritdoc/>
    public void DestroyPipeline(VulkanDeviceCommands device, nint pipelineHandle) =>
        device?.Destroy(
            destroy: device.DestroyPipeline,
            handle: pipelineHandle
        );
}
