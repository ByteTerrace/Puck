using Puck.Shaders;
using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan.Factories;

/// <summary>
/// The default <see cref="IVulkanGraphicsPipelineFactory"/>: it configures a fixed pipeline shape — a
/// vec2-position vertex stream, N scalar combined image-sampler bindings plus an optional storage buffer, straight
/// alpha-over blending, and a triangle-list raster with a dynamic scissor — and returns an owning
/// <see cref="VulkanGraphicsPipeline"/>.
/// </summary>
public sealed class VulkanGraphicsPipelineFactory : IVulkanGraphicsPipelineFactory {
    // The fixed pipeline shape this factory configures: a vec2-position vertex stream, textureSamplerCount scalar
    // combined-image-sampler bindings 0..textureSamplerCount-1 (fragment) plus an optional storage buffer at binding
    // textureSamplerCount (vertex + fragment), straight alpha-over blending, and a triangle-list raster with a
    // dynamic scissor. The underlying pipeline API bakes in none of this.
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

    // ONE binding PER texture (0..textureSamplerCount-1), never a single array-descriptor binding: DXC's
    // vk::combinedImageSampler only fuses a SCALAR Texture2D+SamplerState pair, so a shader sampling several sources
    // declares that many distinct scalar pairs, each at its own binding (the same reason
    // Puck.SdfVm's screen-source kernels declare 32 separate bindings rather than one array of 32). The storage
    // buffer, when present, follows immediately after at binding textureSamplerCount — matching the Direct3D 12
    // factory's identity slot map (DirectXGpuPipelineFactory.BuildLayout), so both backends agree on binding numbers.
    private static IReadOnlyList<VkDescriptorSetLayoutBinding> BuildDescriptorBindings(uint textureSamplerCount, bool enableStorageBuffer) {
        var bindings = new List<VkDescriptorSetLayoutBinding>();

        for (var binding = 0u; (binding < textureSamplerCount); binding++) {
            bindings.Add(item: new VkDescriptorSetLayoutBinding {
                Binding = binding,
                DescriptorCount = 1,
                DescriptorType = DescriptorTypeCombinedImageSampler,
                StageFlags = ShaderStageFragmentBit,
            });
        }

        if (enableStorageBuffer) {
            bindings.Add(item: new VkDescriptorSetLayoutBinding {
                Binding = textureSamplerCount,
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
            pipelineHandle: out var pipelineHandle,
            pipelineLayoutHandle: out var pipelineLayoutHandle,
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
