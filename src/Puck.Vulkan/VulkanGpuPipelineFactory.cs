using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan;

/// <summary>
/// Implements <see cref="IGpuPipelineFactory"/> for Vulkan on its device context. A compute pipeline goes through
/// <see cref="IVulkanComputePipelineApi"/>, which maps each <see cref="GpuComputeBinding"/> to a
/// <c>VkDescriptorSetLayoutBinding</c> at the compute stage and takes the optional push-constant range. A graphics
/// pipeline goes through <see cref="IVulkanGraphicsPipelineFactory"/>, downcasting the render pass and shader modules
/// to their Vulkan-specific types. The graphics pipeline writes
/// every color attachment of the render pass opaquely, tests and writes its depth attachment by the description's
/// comparison, and takes its viewport and scissor dynamically: <see cref="VulkanGpuRecorder.BeginRenderPass"/> sets a
/// negative-height viewport that points clip-space +y at the top of the attachment, as Direct3D 12 does.
/// <para>A description with a layout (<see cref="GpuComputePipelineDescription.Layout"/>,
/// <see cref="GpuGraphicsPipelineDescription.Layout"/>) is created with the pipeline layout
/// <see cref="VulkanPipelineLayouts"/> creates from <see cref="VulkanGroupLayouts.Plan"/>: a set layout per planned set
/// and the planned push range, which the pipeline owns.</para>
/// </summary>
/// <param name="deviceContext">The device context whose current logical device creates every pipeline.</param>
/// <param name="computePipelineApi">The compute pipeline API.</param>
/// <param name="pipelineFactory">The graphics pipeline factory.</param>
/// <param name="allocator">The unmanaged allocator that marshals a planned layout's bindings.</param>
/// <param name="naming">The naming every created pipeline is handed to.</param>
public sealed class VulkanGpuPipelineFactory(IVulkanDeviceContext deviceContext, IVulkanComputePipelineApi computePipelineApi, IVulkanGraphicsPipelineFactory pipelineFactory, IAllocator allocator, GpuObjectNaming naming) : IGpuPipelineFactory {
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

    private IGpuPipeline CreateGraphics(IGpuRenderPass renderPass, IGpuShaderModule vertexShaderModule, IGpuShaderModule fragmentShaderModule, GpuGraphicsPipelineDescription description) {
        ArgumentNullException.ThrowIfNull(description);
        description.ValidateAgainst(renderPass: renderPass);

        var logicalDevice = deviceContext.LogicalDevice;
        var pass = ((VulkanGpuRenderPass)renderPass);
        var vertexShader = ((VulkanShaderModule)vertexShaderModule);
        var fragmentShader = ((VulkanShaderModule)fragmentShaderModule);
        var pushConstantBinding = description.PushConstantBinding;
        var groups = ((description.Layout is null)
            ? null
            : CreateGroupLayouts(
                description: description.RequireLayout(),
                device: logicalDevice.Commands
            ));
        var vkPushConstant = ((pushConstantBinding is null)
            ? null
            : new VulkanPushConstantBinding(
                data: pushConstantBinding.Data,
                offset: pushConstantBinding.Offset,
                stageFlags: ((uint)pushConstantBinding.StageFlags)
            )
        );

        return pipelineFactory.Create(
            enableStorageBuffer: description.EnableStorageBuffer,
            fragmentShaderModule: fragmentShader,
            groups: groups,
            logicalDevice: logicalDevice,
            outputs: new VulkanGraphicsOutputs(
                AlphaBlend: false,
                ColorAttachmentCount: ((uint)pass.Description.Colors.Count),
                DepthCompareOp: ((description.DepthCompare is { } compare)
                    ? ToVkCompareOp(compare: compare)
                    : null)
            ),
            pushConstantBinding: vkPushConstant,
            renderPass: pass.RenderPass,
            textureSamplerCount: description.TextureSamplerCount,
            vertexInput: description.VertexInput,
            vertexShaderModule: vertexShader
        );
    }
}
