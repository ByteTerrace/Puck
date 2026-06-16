using Puck.Shaders;
using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan.Factories;

/// <summary>
/// The default <see cref="IVulkanGraphicsPipelineFactory"/>: it configures a fixed pipeline shape — a
/// vec2-position vertex stream, a combined image-sampler array plus an optional storage buffer, straight
/// alpha-over blending, and a triangle-list raster with a dynamic scissor — and returns an owning
/// <see cref="VulkanGraphicsPipeline"/>.
/// </summary>
public sealed class VulkanGraphicsPipelineFactory : IVulkanGraphicsPipelineFactory {
    // The fixed pipeline shape this factory configures: a vec2-position vertex stream, a
    // combined image-sampler array at binding 0 (fragment) plus a storage buffer at
    // binding 1 (vertex + fragment), straight alpha-over blending, and a triangle-list
    // raster with a dynamic scissor. The underlying pipeline API bakes in none of this.
    private const uint BlendFactorZero = 0;
    private const uint BlendFactorOne = 1;
    private const uint BlendFactorOneMinusSrcAlpha = 7;
    private const uint BlendFactorSrcAlpha = 6;
    private const uint BlendOpAdd = 0;
    private const uint ColorComponentRgbaBits = 0x0000000F;
    private const uint CullModeNone = 0;
    private const uint DescriptorTypeCombinedImageSampler = 1;
    private const uint DescriptorTypeStorageBuffer = 7;
    private const uint DynamicStateScissor = 1;
    private const uint False = 0;
    private const uint FormatR32G32Sfloat = 103;
    private const uint FrontFaceCounterClockwise = 0;
    private const uint PolygonModeFill = 0;
    private const uint PrimitiveTopologyTriangleList = 3;
    private const uint SampleCount1Bit = 0x00000001;
    private const uint ShaderStageFragmentBit = 0x00000010;
    private const uint ShaderStageVertexBit = 0x00000001;
    private const uint StructureTypePipelineMultisampleStateCreateInfo = 24;
    private const uint StructureTypePipelineRasterizationStateCreateInfo = 23;
    private const uint VertexInputRateVertex = 0;
    private const uint VertexPositionStride = 8;

    private readonly IVulkanGraphicsPipelineApi m_graphicsPipelineApi;

    /// <summary>Initializes a new instance of the <see cref="VulkanGraphicsPipelineFactory"/> class.</summary>
    /// <param name="graphicsPipelineApi">The graphics-pipeline API used to create and own the underlying pipeline and its layouts.</param>
    /// <exception cref="ArgumentNullException"><paramref name="graphicsPipelineApi"/> is <see langword="null"/>.</exception>
    public VulkanGraphicsPipelineFactory(IVulkanGraphicsPipelineApi graphicsPipelineApi) {
        ArgumentNullException.ThrowIfNull(argument: graphicsPipelineApi);

        m_graphicsPipelineApi = graphicsPipelineApi;
    }

    private static IReadOnlyList<VkDescriptorSetLayoutBinding> BuildDescriptorBindings(uint textureSamplerCount, bool enableStorageBuffer) {
        var bindings = new List<VkDescriptorSetLayoutBinding> {
            new() {
                Binding = 0,
                DescriptorCount = textureSamplerCount,
                DescriptorType = DescriptorTypeCombinedImageSampler,
                StageFlags = ShaderStageFragmentBit,
            },
        };

        if (enableStorageBuffer) {
            bindings.Add(item: new VkDescriptorSetLayoutBinding {
                Binding = 1,
                DescriptorCount = 1,
                DescriptorType = DescriptorTypeStorageBuffer,
                StageFlags = ShaderStageVertexBit | ShaderStageFragmentBit,
            });
        }

        return bindings;
    }

    /// <inheritdoc/>
    public VulkanGraphicsPipeline Create(
        VulkanLogicalDevice logicalDevice,
        VulkanRenderPass renderPass,
        VulkanSwapchain swapchain,
        VulkanShaderModule vertexShaderModule,
        VulkanShaderModule fragmentShaderModule,
        VulkanPushConstantBinding? pushConstantBinding = null,
        uint textureSamplerCount = 64,
        bool enableStorageBuffer = true
    ) {
        ArgumentNullException.ThrowIfNull(argument: swapchain);

        return Create(
            enableStorageBuffer: enableStorageBuffer,
            fragmentShaderModule: fragmentShaderModule,
            height: swapchain.ImageExtentHeight,
            logicalDevice: logicalDevice,
            pushConstantBinding: pushConstantBinding,
            renderPass: renderPass,
            textureSamplerCount: textureSamplerCount,
            vertexShaderModule: vertexShaderModule,
            width: swapchain.ImageExtentWidth
        );
    }

    /// <inheritdoc/>
    public VulkanGraphicsPipeline Create(
        VulkanLogicalDevice logicalDevice,
        VulkanRenderPass renderPass,
        uint width,
        uint height,
        VulkanShaderModule vertexShaderModule,
        VulkanShaderModule fragmentShaderModule,
        VulkanPushConstantBinding? pushConstantBinding = null,
        uint textureSamplerCount = 64,
        bool enableStorageBuffer = true
    ) {
        ArgumentNullException.ThrowIfNull(argument: logicalDevice);
        ArgumentNullException.ThrowIfNull(argument: renderPass);
        ArgumentNullException.ThrowIfNull(argument: vertexShaderModule);
        ArgumentNullException.ThrowIfNull(argument: fragmentShaderModule);

        if (ShaderStage.Vertex != vertexShaderModule.Stage) {
            throw new InvalidOperationException(message: "Graphics-pipeline creation requires a vertex shader module.");
        }

        if (ShaderStage.Fragment != fragmentShaderModule.Stage) {
            throw new InvalidOperationException(message: "Graphics-pipeline creation requires a fragment shader module.");
        }

        var request = new VulkanGraphicsPipelineCreateRequest(
            ColorBlendAttachments: [
                new VkPipelineColorBlendAttachmentState(
                    blendEnable: 1,
                    colorWriteMask: ColorComponentRgbaBits
                ) {
                    AlphaBlendOp = BlendOpAdd,
                    ColorBlendOp = BlendOpAdd,
                    DstAlphaBlendFactor = BlendFactorZero,
                    DstColorBlendFactor = BlendFactorOneMinusSrcAlpha,
                    SrcAlphaBlendFactor = BlendFactorOne,
                    SrcColorBlendFactor = BlendFactorSrcAlpha,
                },
            ],
            DescriptorBindings: BuildDescriptorBindings(
                enableStorageBuffer: enableStorageBuffer,
                textureSamplerCount: textureSamplerCount
            ),
            DeviceHandle: logicalDevice.Handle,
            DynamicStates: [DynamicStateScissor],
            FragmentShaderModuleHandle: fragmentShaderModule.Handle,
            Height: height,
            Multisample: new VkPipelineMultisampleStateCreateInfo {
                RasterizationSamples = SampleCount1Bit,
                SType = StructureTypePipelineMultisampleStateCreateInfo,
                SampleShadingEnable = False,
            },
            PushConstantSize: (pushConstantBinding?.Size ?? 0),
            PushConstantStageFlags: (pushConstantBinding?.StageFlags ?? 0),
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
            RenderPassHandle: renderPass.Handle,
            Topology: PrimitiveTopologyTriangleList,
            VertexAttributes: [
                new VkVertexInputAttributeDescription {
                    Binding = 0,
                    Format = FormatR32G32Sfloat,
                    Location = 0,
                    Offset = 0,
                },
            ],
            VertexBindings: [
                new VkVertexInputBindingDescription {
                    Binding = 0,
                    InputRate = VertexInputRateVertex,
                    Stride = VertexPositionStride,
                },
            ],
            VertexShaderModuleHandle: vertexShaderModule.Handle,
            Width: width
        );
        var result = m_graphicsPipelineApi.CreateGraphicsPipeline(
            descriptorSetLayoutHandle: out var descriptorSetLayoutHandle,
            pipelineLayoutHandle: out var pipelineLayoutHandle,
            pipelineHandle: out var pipelineHandle,
            request: request
        );

        result.ThrowIfFailed(operation: "vkCreateGraphicsPipelines");

        if (0 == descriptorSetLayoutHandle) {
            throw new InvalidOperationException(message: "vkCreateGraphicsPipelines returned success without a valid descriptor-set-layout handle.");
        }

        if (0 == pipelineLayoutHandle) {
            throw new InvalidOperationException(message: "vkCreateGraphicsPipelines returned success without a valid pipeline-layout handle.");
        }

        if (0 == pipelineHandle) {
            throw new InvalidOperationException(message: "vkCreateGraphicsPipelines returned success without a valid graphics-pipeline handle.");
        }

        return new(
            descriptorSetLayoutHandle: descriptorSetLayoutHandle,
            deviceHandle: logicalDevice.Handle,
            graphicsPipelineApi: m_graphicsPipelineApi,
            layoutHandle: pipelineLayoutHandle,
            pipelineHandle: pipelineHandle
        );
    }
}
