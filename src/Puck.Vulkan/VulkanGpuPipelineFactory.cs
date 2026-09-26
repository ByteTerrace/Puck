using Puck.Shaders;
using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan;

/// <summary>
/// Implements <see cref="IGpuPipelineFactory"/> for Vulkan on its device context, the one factory every Vulkan pipeline
/// is created through, the presenter's blit included. A compute pipeline goes through
/// <see cref="IVulkanComputePipelineApi"/>, which maps each <see cref="GpuComputeBinding"/> to a
/// <c>VkDescriptorSetLayoutBinding</c> at the compute stage and takes the optional push-constant range. A graphics
/// pipeline goes through <see cref="IVulkanGraphicsPipelineApi"/>: it reads its vertex attributes from the description,
/// writes every color attachment of the render pass opaquely, tests and writes its depth attachment by the description's
/// comparison, draws an unculled triangle list, and takes its viewport and scissor dynamically:
/// <see cref="VulkanGpuRecorder.BeginRenderPass"/> sets a negative-height viewport that points clip-space +y at the top
/// of the attachment, as Direct3D 12 does.
/// <para>A description with a layout (<see cref="GpuComputePipelineDescription.Layout"/>, and every
/// <see cref="GpuGraphicsPipelineDescription"/>) is created with the pipeline layout <see cref="VulkanPipelineLayouts"/>
/// creates from <see cref="VulkanGroupLayouts.Plan"/>: a set layout per planned set and the planned push range, which
/// the pipeline owns.</para>
/// </summary>
/// <param name="deviceContext">The device context whose current logical device creates every pipeline.</param>
/// <param name="computePipelineApi">The compute pipeline API.</param>
/// <param name="graphicsPipelineApi">The graphics pipeline API.</param>
/// <param name="allocator">The unmanaged allocator that marshals a planned layout's bindings.</param>
/// <param name="naming">The naming every created pipeline is handed to.</param>
public sealed class VulkanGpuPipelineFactory(IVulkanDeviceContext deviceContext, IVulkanComputePipelineApi computePipelineApi, IVulkanGraphicsPipelineApi graphicsPipelineApi, IAllocator allocator, GpuObjectNaming naming) : IGpuPipelineFactory {
    private const uint BlendFactorOne = 1;
    private const uint BlendFactorZero = 0;
    private const uint BlendOpAdd = 0;
    private const uint ColorComponentRgbaBits = 0x0000000F;
    private const uint CullModeNone = 0;
    private const uint DynamicStateScissor = 1;
    private const uint DynamicStateViewport = 0;
    private const uint False = 0;
    private const uint FormatR32G32B32A32Sfloat = 109;
    private const uint FormatR32G32B32Sfloat = 106;
    private const uint FormatR32G32Sfloat = 103;
    private const uint FrontFaceCounterClockwise = 0;
    private const uint PolygonModeFill = 0;
    private const uint PrimitiveTopologyTriangleList = 3;
    private const uint SampleCount1Bit = 0x00000001;
    private const uint StructureTypePipelineDepthStencilStateCreateInfo = 25;
    private const uint StructureTypePipelineMultisampleStateCreateInfo = 24;
    private const uint StructureTypePipelineRasterizationStateCreateInfo = 23;
    private const uint True = 1;
    private const uint VertexInputRateVertex = 0;

    private VulkanGroupPipelineLayout CreateGroupLayouts(VulkanDeviceCommands device, GpuPipelineLayoutDescription description) {
        VulkanPipelineLayouts.Create(
            allocator: allocator,
            device: device,
            groups: VulkanGroupLayouts.Plan(description: description),
            layouts: out var layouts
        ).ThrowIfFailed(operation: "vkCreatePipelineLayout");

        return layouts!;
    }

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
    public IGpuComputePipeline Create(IGpuShaderModule computeShaderModule, GpuComputePipelineDescription description, in GpuObjectName name) {
        var pipeline = CreateCompute(
            computeShaderModule: computeShaderModule,
            description: description
        );

        naming.Name(
            handle: pipeline.Handle,
            kind: GpuObjectKind.Pipeline,
            name: in name
        );

        return pipeline;
    }

    private VulkanGpuComputePipeline CreateCompute(IGpuShaderModule computeShaderModule, GpuComputePipelineDescription description) {
        // description.SamplerFilter is a Direct3D 12 static-sampler concern; on Vulkan the sampler is a bound
        // descriptor whose filter the caller chose at CreateSampler time, so the combined-image-sampler layout
        // binding is filter-agnostic.
        ArgumentNullException.ThrowIfNull(computeShaderModule);
        ArgumentNullException.ThrowIfNull(description);

        var logicalDevice = deviceContext.LogicalDevice;
        var device = logicalDevice.Commands;

        if (description.Layout is not null) {
            var groups = CreateGroupLayouts(
                description: description.RequireLayout(),
                device: device
            );
            var groupResult = computePipelineApi.CreateComputePipeline(
                descriptorSetLayoutHandle: out _,
                pipelineHandle: out var groupPipeline,
                pipelineLayoutHandle: out _,
                request: new VulkanComputePipelineCreateRequest(
                    DescriptorBindings: [],
                    Device: device,
                    PipelineCache: logicalDevice.PipelineCache,
                    PipelineLayoutHandle: groups.PipelineLayoutHandle,
                    PushConstantSize: 0,
                    PushConstantStageFlags: 0,
                    ShaderModuleHandle: computeShaderModule.Handle
                )
            );

            if (!groupResult.IsSuccess()) {
                VulkanPipelineLayouts.Destroy(
                    device: device,
                    layouts: groups
                );
            }

            groupResult.ThrowIfFailed(operation: "vkCreateComputePipelines");

            return new VulkanGpuComputePipeline(
                api: computePipelineApi,
                descriptorSetLayoutHandle: 0,
                device: device,
                groupLayoutHandles: groups.SetLayoutHandles,
                layoutHandle: groups.PipelineLayoutHandle,
                pipelineHandle: groupPipeline,
                setGroups: logicalDevice.SetGroups
            );
        }

        var bindings = description.Bindings;
        var pushConstantBinding = description.PushConstantBinding;

        ArgumentNullException.ThrowIfNull(bindings);
        GpuComputeBinding.ValidateSet(bindings: bindings);

        var descriptorBindings = new VkDescriptorSetLayoutBinding[bindings.Count];

        for (var index = 0; (index < bindings.Count); index++) {
            descriptorBindings[index] = new VkDescriptorSetLayoutBinding {
                Binding = bindings[index].Binding,
                DescriptorCount = bindings[index].Count,
                // A storage image and a sampled image are each their own type; both storage-buffer kinds (read and
                // read-write) are a Vulkan storage buffer — the read/write distinction only matters to the Direct3D 12
                // SRV/UAV split.
                DescriptorType = bindings[index].Kind switch {
                    GpuComputeBindingKind.StorageImage => VulkanDescriptorType.StorageImage,
                    GpuComputeBindingKind.SampledImage => VulkanDescriptorType.CombinedImageSampler,
                    _ => VulkanDescriptorType.StorageBuffer,
                },
                StageFlags = ((uint)GpuShaderStage.Compute),
            };
        }

        computePipelineApi.CreateComputePipeline(
            request: new VulkanComputePipelineCreateRequest(
                Device: device,
                ShaderModuleHandle: computeShaderModule.Handle,
                DescriptorBindings: descriptorBindings,
                PipelineCache: logicalDevice.PipelineCache,
                PushConstantSize: (pushConstantBinding?.Size ?? 0u),
                PushConstantStageFlags: ((uint)(pushConstantBinding?.StageFlags ?? GpuShaderStage.None))
            ),
            descriptorSetLayoutHandle: out var setLayout,
            pipelineLayoutHandle: out var pipelineLayout,
            pipelineHandle: out var pipeline
        ).ThrowIfFailed(operation: "vkCreateComputePipelines");

        return new VulkanGpuComputePipeline(
            api: computePipelineApi,
            descriptorSetLayoutHandle: setLayout,
            device: device,
            layoutHandle: pipelineLayout,
            pipelineHandle: pipeline
        );
    }

    /// <inheritdoc/>
    public IGpuPipeline Create(
        IGpuRenderPass renderPass,
        IGpuShaderModule vertexShaderModule,
        IGpuShaderModule fragmentShaderModule,
        GpuGraphicsPipelineDescription description,
        in GpuObjectName name
    ) {
        var pipeline = CreateGraphics(
            description: description,
            fragmentShaderModule: fragmentShaderModule,
            renderPass: renderPass,
            vertexShaderModule: vertexShaderModule
        );

        naming.Name(
            handle: pipeline.Handle,
            kind: GpuObjectKind.Pipeline,
            name: in name
        );

        return pipeline;
    }

    private VulkanGraphicsPipeline CreateGraphics(IGpuRenderPass renderPass, IGpuShaderModule vertexShaderModule, IGpuShaderModule fragmentShaderModule, GpuGraphicsPipelineDescription description) {
        ArgumentNullException.ThrowIfNull(description);
        description.ValidateAgainst(renderPass: renderPass);

        var logicalDevice = deviceContext.LogicalDevice;
        var pass = ((VulkanGpuRenderPass)renderPass);
        var vertexShader = ((VulkanShaderModule)vertexShaderModule);
        var fragmentShader = ((VulkanShaderModule)fragmentShaderModule);

        if (ShaderStage.Vertex != vertexShader.Stage) {
            throw new InvalidOperationException(message: "Graphics-pipeline creation requires a vertex shader module.");
        }

        if (ShaderStage.Fragment != fragmentShader.Stage) {
            throw new InvalidOperationException(message: "Graphics-pipeline creation requires a fragment shader module.");
        }

        var vertexInput = description.VertexInput;

        if (
            (vertexInput.Attributes.Count > 0) &&
            (vertexInput.StrideBytes == 0)
        ) {
            throw new ArgumentException(
                message: "A vertex input layout with attributes requires a non-zero stride.",
                paramName: nameof(description)
            );
        }

        var vertexAttributes = new VkVertexInputAttributeDescription[vertexInput.Attributes.Count];

        for (var index = 0; (index < vertexAttributes.Length); index++) {
            var attribute = vertexInput.Attributes[index];

            vertexAttributes[index] = new VkVertexInputAttributeDescription {
                Binding = 0,
                Format = ToVkVertexFormat(format: attribute.Format),
                Location = attribute.Location,
                Offset = attribute.OffsetBytes,
            };
        }

        // Every color attachment is written opaquely: a fragment replaces the texel.
        var blendAttachments = new VkPipelineColorBlendAttachmentState[pass.Description.Colors.Count];

        for (var index = 0; (index < blendAttachments.Length); index++) {
            blendAttachments[index] = new VkPipelineColorBlendAttachmentState(
                blendEnable: 0,
                colorWriteMask: ColorComponentRgbaBits
            ) {
                AlphaBlendOp = BlendOpAdd,
                ColorBlendOp = BlendOpAdd,
                DstAlphaBlendFactor = BlendFactorZero,
                DstColorBlendFactor = BlendFactorZero,
                SrcAlphaBlendFactor = BlendFactorOne,
                SrcColorBlendFactor = BlendFactorOne,
            };
        }

        var groups = CreateGroupLayouts(
            description: description.RequireLayout(),
            device: logicalDevice.Commands
        );
        var result = graphicsPipelineApi.CreateGraphicsPipeline(
            pipelineHandle: out var pipelineHandle,
            request: new VulkanGraphicsPipelineCreateRequest(
                ColorBlendAttachments: blendAttachments,
                DepthStencil: ((description.DepthCompare is { } compare)
                    ? new VkPipelineDepthStencilStateCreateInfo {
                        DepthCompareOp = ToVkCompareOp(compare: compare),
                        DepthTestEnable = True,
                        DepthWriteEnable = True,
                        SType = StructureTypePipelineDepthStencilStateCreateInfo,
                    }
                    : null),
                Device: logicalDevice.Commands,
                DynamicStates: [DynamicStateViewport, DynamicStateScissor],
                FragmentShaderModuleHandle: fragmentShader.Handle,
                Multisample: new VkPipelineMultisampleStateCreateInfo {
                    RasterizationSamples = SampleCount1Bit,
                    SType = StructureTypePipelineMultisampleStateCreateInfo,
                    SampleShadingEnable = False,
                },
                PipelineCache: logicalDevice.PipelineCache,
                PipelineLayoutHandle: groups.PipelineLayoutHandle,
                Rasterization: new VkPipelineRasterizationStateCreateInfo {
                    CullMode = CullModeNone,
                    DepthBiasEnable = False,
                    DepthClampEnable = False,
                    FrontFace = FrontFaceCounterClockwise,
                    LineWidth = 1f,
                    PolygonMode = PolygonModeFill,
                    RasterizerDiscardEnable = False,
                    SType = StructureTypePipelineRasterizationStateCreateInfo,
                },
                RenderPassHandle: pass.RenderPass.Handle,
                Topology: PrimitiveTopologyTriangleList,
                VertexAttributes: vertexAttributes,
                VertexBindings: ((vertexAttributes.Length == 0)
                    ? []
                    : [new VkVertexInputBindingDescription {
                        Binding = 0,
                        InputRate = VertexInputRateVertex,
                        Stride = vertexInput.StrideBytes,
                    }]),
                VertexShaderModuleHandle: vertexShader.Handle
            )
        );

        if (!result.IsSuccess()) {
            VulkanPipelineLayouts.Destroy(
                device: logicalDevice.Commands,
                layouts: groups
            );
        }

        result.ThrowIfFailed(operation: "vkCreateGraphicsPipelines");

        if (0 == pipelineHandle) {
            throw new InvalidOperationException(message: "vkCreateGraphicsPipelines returned success without a valid graphics-pipeline handle.");
        }

        return new VulkanGraphicsPipeline(
            descriptorSetLayoutHandle: 0,
            device: logicalDevice.Commands,
            graphicsPipelineApi: graphicsPipelineApi,
            groupLayoutHandles: groups.SetLayoutHandles,
            layoutHandle: groups.PipelineLayoutHandle,
            pipelineHandle: pipelineHandle,
            setGroups: logicalDevice.SetGroups
        );
    }
    private static uint ToVkVertexFormat(GpuVertexFormat format) => format switch {
        GpuVertexFormat.R32G32Float => FormatR32G32Sfloat,
        GpuVertexFormat.R32G32B32Float => FormatR32G32B32Sfloat,
        GpuVertexFormat.R32G32B32A32Float => FormatR32G32B32A32Sfloat,
        _ => throw new ArgumentOutOfRangeException(
            actualValue: format,
            message: "The vertex attribute format is not defined.",
            paramName: nameof(format)
        ),
    };
}
