using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan;

/// <summary>Native <see cref="IVulkanDescriptorApi"/>: generic descriptor-pool/set/sampler
/// management loaded through the same per-device proc-address pattern as the other native
/// resource APIs. Carries no policy — descriptor types, counts, sampler parameters, and
/// image layouts are all the caller's.</summary>
public unsafe sealed class VulkanNativeDescriptorApi : IVulkanDescriptorApi {
    private const uint StructureTypeDescriptorPoolCreateInfo = 33;
    private const uint StructureTypeDescriptorSetAllocateInfo = 34;
    private const uint StructureTypeSamplerCreateInfo = 31;
    private const uint StructureTypeWriteDescriptorSet = 35;

    /// <inheritdoc/>
    public nint AllocateSet(VulkanDescriptorSetAllocateRequest request) {
        var layoutHandle = request.DescriptorSetLayoutHandle;
        nint setHandle;

        var allocateInfo = new VkDescriptorSetAllocateInfo {
            DescriptorPool = request.PoolHandle,
            DescriptorSetCount = 1,
            PSetLayouts = ((nint)(&layoutHandle)),
            SType = StructureTypeDescriptorSetAllocateInfo,
        };

        request.Device.AllocateDescriptorSets(
            request.Device.Handle,
            in allocateInfo,
            ((nint)(&setHandle))
        ).ThrowIfFailed(operation: "vkAllocateDescriptorSets");

        return setHandle;
    }
    /// <inheritdoc/>
    public nint CreatePool(VulkanDescriptorPoolCreateRequest request) {
        var poolSizes = request.PoolSizes.Span;
        Span<VkDescriptorPoolSize> sizes = stackalloc VkDescriptorPoolSize[poolSizes.Length];

        for (var index = 0; (index < poolSizes.Length); index++) {
            sizes[index] = new VkDescriptorPoolSize {
                DescriptorCount = poolSizes[index].DescriptorCount,
                Type = poolSizes[index].DescriptorType,
            };
        }

        fixed (VkDescriptorPoolSize* sizesPointer = sizes) {
            var createInfo = new VkDescriptorPoolCreateInfo {
                Flags = request.Flags,
                MaxSets = request.MaxSets,
                PPoolSizes = ((nint)sizesPointer),
                PoolSizeCount = ((uint)poolSizes.Length),
                SType = StructureTypeDescriptorPoolCreateInfo,
            };

            request.Device.CreateDescriptorPool(
                request.Device.Handle,
                in createInfo,
                0,
                out var poolHandle
            ).ThrowIfFailed(operation: "vkCreateDescriptorPool");

            return poolHandle;
        }
    }
    /// <inheritdoc/>
    public nint CreateSampler(VulkanSamplerCreateRequest request) {
        var createInfo = new VkSamplerCreateInfo {
            AddressModeU = request.AddressModeU,
            AddressModeV = request.AddressModeV,
            AddressModeW = request.AddressModeW,
            AnisotropyEnable = request.AnisotropyEnable,
            BorderColor = request.BorderColor,
            CompareEnable = request.CompareEnable,
            CompareOp = request.CompareOp,
            Flags = request.Flags,
            MagFilter = request.MagFilter,
            MaxAnisotropy = request.MaxAnisotropy,
            MaxLod = request.MaxLod,
            MinFilter = request.MinFilter,
            MinLod = request.MinLod,
            MipLodBias = request.MipLodBias,
            MipmapMode = request.MipmapMode,
            SType = StructureTypeSamplerCreateInfo,
            UnnormalizedCoordinates = request.UnnormalizedCoordinates,
        };

        request.Device.CreateSampler(
            request.Device.Handle,
            in createInfo,
            0,
            out var samplerHandle
        ).ThrowIfFailed(operation: "vkCreateSampler");

        return samplerHandle;
    }
    /// <inheritdoc/>
    public void DestroyPool(VulkanDeviceCommands device, nint poolHandle) =>
        device?.Destroy(
            destroy: device.DestroyDescriptorPool,
            handle: poolHandle
        );
    /// <inheritdoc/>
    public void DestroySampler(VulkanDeviceCommands device, nint samplerHandle) =>
        device?.Destroy(
            destroy: device.DestroySampler,
            handle: samplerHandle
        );
    /// <inheritdoc/>
    public void WriteBuffer(VulkanDescriptorBufferWriteRequest request) {
        var bufferInfo = new VkDescriptorBufferInfo {
            Buffer = request.BufferHandle,
            Offset = request.BufferOffset,
            Range = request.BufferRange,
        };
        var write = new VkWriteDescriptorSet {
            DescriptorCount = 1,
            DescriptorType = request.DescriptorType,
            DstArrayElement = request.ArrayElement,
            DstBinding = request.Binding,
            DstSet = request.DescriptorSetHandle,
            PBufferInfo = ((nint)(&bufferInfo)),
            SType = StructureTypeWriteDescriptorSet,
        };

        request.Device.UpdateDescriptorSets(
            request.Device.Handle,
            1,
            ((nint)(&write)),
            0,
            0
        );
    }
    /// <inheritdoc/>
    public void WriteImage(VulkanDescriptorImageWriteRequest request) {
        var imageInfo = new VkDescriptorImageInfo {
            ImageLayout = request.ImageLayout,
            ImageView = request.ImageViewHandle,
            Sampler = request.SamplerHandle,
        };
        var write = new VkWriteDescriptorSet {
            DescriptorCount = 1,
            DescriptorType = request.DescriptorType,
            DstArrayElement = request.ArrayElement,
            DstBinding = request.Binding,
            DstSet = request.DescriptorSetHandle,
            PImageInfo = ((nint)(&imageInfo)),
            SType = StructureTypeWriteDescriptorSet,
        };

        request.Device.UpdateDescriptorSets(
            request.Device.Handle,
            1,
            ((nint)(&write)),
            0,
            0
        );
    }
}
