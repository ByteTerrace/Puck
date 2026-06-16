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

    private readonly Lock m_syncRoot = new();
    private delegate* unmanaged[Cdecl]<nint, byte*, nint> m_getDeviceProcAddr;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<nint, DevicePointers> m_pointers = new();

    /// <inheritdoc/>
    public nint AllocateSet(VulkanDescriptorSetAllocateRequest request) {
        var pointers = GetPointers(deviceHandle: request.DeviceHandle);
        var layoutHandle = request.DescriptorSetLayoutHandle;
        nint setHandle;

        var allocateInfo = new VkDescriptorSetAllocateInfo {
            DescriptorPool = request.PoolHandle,
            DescriptorSetCount = 1,
            PSetLayouts = (nint)(&layoutHandle),
            SType = StructureTypeDescriptorSetAllocateInfo,
        };

        pointers.AllocateDescriptorSets(
            request.DeviceHandle,
            in allocateInfo,
            (nint)(&setHandle)
        ).ThrowIfFailed(operation: "vkAllocateDescriptorSets");

        return setHandle;
    }
    /// <inheritdoc/>
    public nint CreatePool(VulkanDescriptorPoolCreateRequest request) {
        var pointers = GetPointers(deviceHandle: request.DeviceHandle);
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
                PPoolSizes = (nint)sizesPointer,
                PoolSizeCount = (uint)poolSizes.Length,
                SType = StructureTypeDescriptorPoolCreateInfo,
            };

            pointers.CreateDescriptorPool(
                request.DeviceHandle,
                in createInfo,
                0,
                out var poolHandle
            ).ThrowIfFailed(operation: "vkCreateDescriptorPool");

            return poolHandle;
        }
    }
    /// <inheritdoc/>
    public nint CreateSampler(VulkanSamplerCreateRequest request) {
        var pointers = GetPointers(deviceHandle: request.DeviceHandle);
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

        pointers.CreateSampler(
            request.DeviceHandle,
            in createInfo,
            0,
            out var samplerHandle
        ).ThrowIfFailed(operation: "vkCreateSampler");

        return samplerHandle;
    }
    /// <inheritdoc/>
    public void DestroyPool(nint deviceHandle, nint poolHandle) {
        if (
            (0 == deviceHandle) ||
            (0 == poolHandle)
        ) {
            return;
        }

        GetPointers(deviceHandle: deviceHandle).DestroyDescriptorPool(
            deviceHandle,
            poolHandle,
            0
        );
    }
    /// <inheritdoc/>
    public void DestroySampler(nint deviceHandle, nint samplerHandle) {
        if (
            (0 == deviceHandle) ||
            (0 == samplerHandle)
        ) {
            return;
        }

        GetPointers(deviceHandle: deviceHandle).DestroySampler(
            deviceHandle,
            samplerHandle,
            0
        );
    }
    /// <inheritdoc/>
    public void WriteBuffer(VulkanDescriptorBufferWriteRequest request) {
        var pointers = GetPointers(deviceHandle: request.DeviceHandle);
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
            PBufferInfo = (nint)(&bufferInfo),
            SType = StructureTypeWriteDescriptorSet,
        };

        pointers.UpdateDescriptorSets(
            request.DeviceHandle,
            1,
            (nint)(&write),
            0,
            0
        );
    }
    /// <inheritdoc/>
    public void WriteImage(VulkanDescriptorImageWriteRequest request) {
        var pointers = GetPointers(deviceHandle: request.DeviceHandle);
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
            PImageInfo = (nint)(&imageInfo),
            SType = StructureTypeWriteDescriptorSet,
        };

        pointers.UpdateDescriptorSets(
            request.DeviceHandle,
            1,
            (nint)(&write),
            0,
            0
        );
    }

    private struct DevicePointers {
        public delegate* unmanaged[Cdecl]<nint, in VkDescriptorSetAllocateInfo, nint, VkResult> AllocateDescriptorSets;
        public delegate* unmanaged[Cdecl]<nint, in VkDescriptorPoolCreateInfo, nint, out nint, VkResult> CreateDescriptorPool;
        public delegate* unmanaged[Cdecl]<nint, in VkSamplerCreateInfo, nint, out nint, VkResult> CreateSampler;
        public delegate* unmanaged[Cdecl]<nint, nint, nint, void> DestroyDescriptorPool;
        public delegate* unmanaged[Cdecl]<nint, nint, nint, void> DestroySampler;
        public delegate* unmanaged[Cdecl]<nint, uint, nint, uint, nint, void> UpdateDescriptorSets;
    }

    private DevicePointers GetPointers(nint deviceHandle) {
        if (m_pointers.TryGetValue(
            key: deviceHandle,
            value: out var pointers
        )) {
            return pointers;
        }

        var getAddr = GetDeviceProcAddr();
        DevicePointers pNew = default;

        fixed (byte* pName = "vkAllocateDescriptorSets"u8) {
            pNew.AllocateDescriptorSets = (delegate* unmanaged[Cdecl]<nint, in VkDescriptorSetAllocateInfo, nint, VkResult>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkCreateDescriptorPool"u8) {
            pNew.CreateDescriptorPool = (delegate* unmanaged[Cdecl]<nint, in VkDescriptorPoolCreateInfo, nint, out nint, VkResult>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkCreateSampler"u8) {
            pNew.CreateSampler = (delegate* unmanaged[Cdecl]<nint, in VkSamplerCreateInfo, nint, out nint, VkResult>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkDestroyDescriptorPool"u8) {
            pNew.DestroyDescriptorPool = (delegate* unmanaged[Cdecl]<nint, nint, nint, void>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkDestroySampler"u8) {
            pNew.DestroySampler = (delegate* unmanaged[Cdecl]<nint, nint, nint, void>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkUpdateDescriptorSets"u8) {
            pNew.UpdateDescriptorSets = (delegate* unmanaged[Cdecl]<nint, uint, nint, uint, nint, void>)getAddr(
                deviceHandle,
                pName
            );
        }

        m_pointers[deviceHandle] = pNew;
        return pNew;
    }
    private delegate* unmanaged[Cdecl]<nint, byte*, nint> GetDeviceProcAddr() {
        lock (m_syncRoot) {
            if (m_getDeviceProcAddr is not null) {
                return m_getDeviceProcAddr;
            }

            var export = VulkanNativeLibrary.GetExport(functionName: "vkGetDeviceProcAddr");

            m_getDeviceProcAddr = (delegate* unmanaged[Cdecl]<nint, byte*, nint>)export;
            return m_getDeviceProcAddr;
        }
    }
}
