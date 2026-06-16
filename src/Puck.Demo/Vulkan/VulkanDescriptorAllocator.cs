using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Messages;

namespace Puck.Demo;

/// <summary>
/// The demo's descriptor policy: the small set of opinionated choices the showcase makes on top of the
/// generic <see cref="IVulkanDescriptorApi"/> — one-set pools sized for its pipeline layout (a combined
/// image sampler plus a storage buffer), a linear clamp-to-edge sampler for compositing viewport textures,
/// and writes that assume the shader-read-only image layout. All of these are demo concerns; the library
/// stays type-, count-, and layout-agnostic.
/// </summary>
internal sealed class VulkanDescriptorAllocator(IVulkanDescriptorApi descriptorApi) {
    private const uint DescriptorTypeCombinedImageSampler = 1;
    private const uint DescriptorTypeStorageBuffer = 7;
    private const uint FilterLinear = 1;
    private const uint ImageLayoutShaderReadOnlyOptimal = 5;
    private const uint SamplerAddressModeClampToEdge = 2;
    private const uint SamplerMipmapModeLinear = 1;

    /// <summary>Allocates a single descriptor set against a pipeline's descriptor-set layout.</summary>
    public nint AllocateSet(nint deviceHandle, nint poolHandle, nint descriptorSetLayoutHandle) {
        return descriptorApi.AllocateSet(request: new VulkanDescriptorSetAllocateRequest(
            DescriptorSetLayoutHandle: descriptorSetLayoutHandle,
            DeviceHandle: deviceHandle,
            PoolHandle: poolHandle
        ));
    }
    /// <summary>Creates a one-set descriptor pool with capacity for the given descriptor counts.</summary>
    public nint CreatePool(nint deviceHandle, uint maxCombinedImageSamplers, uint maxStorageBuffers) {
        var sizes = new VulkanDescriptorPoolSize[(((maxCombinedImageSamplers > 0)
            ? 1
            : 0) + ((maxStorageBuffers > 0)
            ? 1
            : 0))];
        var sizeCount = 0;

        if (maxCombinedImageSamplers > 0) {
            sizes[sizeCount++] = new VulkanDescriptorPoolSize(
                DescriptorCount: maxCombinedImageSamplers,
                DescriptorType: DescriptorTypeCombinedImageSampler
            );
        }

        if (maxStorageBuffers > 0) {
            sizes[sizeCount++] = new VulkanDescriptorPoolSize(
                DescriptorCount: maxStorageBuffers,
                DescriptorType: DescriptorTypeStorageBuffer
            );
        }

        return descriptorApi.CreatePool(request: new VulkanDescriptorPoolCreateRequest(
            DeviceHandle: deviceHandle,
            Flags: 0,
            MaxSets: 1,
            PoolSizes: sizes
        ));
    }
    /// <summary>Creates a linear, clamp-to-edge sampler for compositing viewport textures.</summary>
    public nint CreateSampler(nint deviceHandle) {
        return descriptorApi.CreateSampler(request: new VulkanSamplerCreateRequest(
            AddressModeU: SamplerAddressModeClampToEdge,
            AddressModeV: SamplerAddressModeClampToEdge,
            AddressModeW: SamplerAddressModeClampToEdge,
            AnisotropyEnable: 0,
            BorderColor: 0,
            CompareEnable: 0,
            CompareOp: 0,
            DeviceHandle: deviceHandle,
            Flags: 0,
            MagFilter: FilterLinear,
            MaxAnisotropy: 0f,
            MaxLod: 0f,
            MinFilter: FilterLinear,
            MinLod: 0f,
            MipLodBias: 0f,
            MipmapMode: SamplerMipmapModeLinear,
            UnnormalizedCoordinates: 0
        ));
    }
    /// <summary>Destroys a pool (and every set allocated from it).</summary>
    public void DestroyPool(nint deviceHandle, nint poolHandle) {
        descriptorApi.DestroyPool(
            deviceHandle: deviceHandle,
            poolHandle: poolHandle
        );
    }
    /// <summary>Destroys a sampler created by <see cref="CreateSampler"/>.</summary>
    public void DestroySampler(nint deviceHandle, nint samplerHandle) {
        descriptorApi.DestroySampler(
            deviceHandle: deviceHandle,
            samplerHandle: samplerHandle
        );
    }
    /// <summary>Points one array element of a combined-image-sampler binding at an image view + sampler
    /// (a rendered viewport texture the compositor samples). The image is expected in shader-read-only
    /// layout.</summary>
    public void WriteCombinedImageSampler(nint deviceHandle, nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle, nint samplerHandle) {
        descriptorApi.WriteImage(request: new VulkanDescriptorImageWriteRequest(
            ArrayElement: arrayElement,
            Binding: binding,
            DescriptorSetHandle: descriptorSetHandle,
            DescriptorType: DescriptorTypeCombinedImageSampler,
            DeviceHandle: deviceHandle,
            ImageLayout: ImageLayoutShaderReadOnlyOptimal,
            ImageViewHandle: imageViewHandle,
            SamplerHandle: samplerHandle
        ));
    }
    /// <summary>Points a storage-buffer binding of a set at a buffer.</summary>
    public void WriteStorageBuffer(nint deviceHandle, nint descriptorSetHandle, uint binding, nint bufferHandle, ulong bufferSize) {
        descriptorApi.WriteBuffer(request: new VulkanDescriptorBufferWriteRequest(
            ArrayElement: 0,
            Binding: binding,
            BufferHandle: bufferHandle,
            BufferOffset: 0,
            BufferRange: bufferSize,
            DescriptorSetHandle: descriptorSetHandle,
            DescriptorType: DescriptorTypeStorageBuffer,
            DeviceHandle: deviceHandle
        ));
    }
}
