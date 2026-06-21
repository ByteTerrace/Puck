using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan;

/// <summary>
/// Implements <see cref="IGpuComputePipelineFactory"/> for Vulkan by forwarding to
/// <see cref="IVulkanComputePipelineApi"/>: it maps each <see cref="GpuComputeBinding"/> to a
/// <c>VkDescriptorSetLayoutBinding</c> (a storage image or storage buffer at the compute stage) and the optional
/// push-constant range to the create request.
/// </summary>
public sealed class VulkanGpuComputePipelineFactory(IVulkanComputePipelineApi computePipelineApi) : IGpuComputePipelineFactory {
    /// <inheritdoc/>
    public IGpuComputePipeline Create(IGpuDeviceContext deviceContext, IGpuShaderModule computeShaderModule, IReadOnlyList<GpuComputeBinding> bindings, GpuPushConstantBinding? pushConstantBinding, uint samplerFilter = GpuSamplerFilter.Linear) {
        // samplerFilter is a Direct3D 12 static-sampler concern; on Vulkan the sampler is a bound descriptor whose
        // filter the caller chose at CreateSampler time, so the combined-image-sampler layout binding is filter-agnostic.
        _ = samplerFilter;

        ArgumentNullException.ThrowIfNull(deviceContext);
        ArgumentNullException.ThrowIfNull(computeShaderModule);
        ArgumentNullException.ThrowIfNull(bindings);

        var deviceHandle = ((IVulkanDeviceContext)deviceContext).LogicalDevice.Handle;
        var descriptorBindings = new VkDescriptorSetLayoutBinding[bindings.Count];

        for (var index = 0; (index < bindings.Count); index++) {
            descriptorBindings[index] = new VkDescriptorSetLayoutBinding {
                Binding = bindings[index].Binding,
                DescriptorCount = ((bindings[index].Count > 0) ? bindings[index].Count : 1),
                // A storage image and an acceleration structure are each their own type; both storage-buffer kinds
                // (read and read-write) are a Vulkan storage buffer — the read/write distinction only matters to the
                // Direct3D 12 SRV/UAV split.
                DescriptorType = bindings[index].Kind switch {
                    GpuComputeBindingKind.StorageImage => VulkanDescriptorType.StorageImage,
                    GpuComputeBindingKind.SampledImage => VulkanDescriptorType.CombinedImageSampler,
                    GpuComputeBindingKind.AccelerationStructure => VulkanDescriptorType.AccelerationStructure,
                    _ => VulkanDescriptorType.StorageBuffer,
                },
                StageFlags = GpuShaderStage.Compute,
            };
        }

        computePipelineApi.CreateComputePipeline(
            request: new VulkanComputePipelineCreateRequest(
                DeviceHandle: deviceHandle,
                ShaderModuleHandle: computeShaderModule.Handle,
                DescriptorBindings: descriptorBindings,
                PushConstantSize: (pushConstantBinding?.Size ?? 0u),
                PushConstantStageFlags: (pushConstantBinding?.StageFlags ?? 0u)
            ),
            descriptorSetLayoutHandle: out var setLayout,
            pipelineLayoutHandle: out var pipelineLayout,
            pipelineHandle: out var pipeline
        ).ThrowIfFailed(operation: "vkCreateComputePipelines");

        return new VulkanGpuComputePipeline(
            api: computePipelineApi,
            descriptorSetLayoutHandle: setLayout,
            deviceHandle: deviceHandle,
            layoutHandle: pipelineLayout,
            pipelineHandle: pipeline
        );
    }
}
