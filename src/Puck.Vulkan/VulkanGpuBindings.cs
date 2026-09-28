using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan;

/// <summary>
/// Implements <see cref="IGpuBindings"/> by forwarding to <see cref="VulkanDescriptorAllocator"/> against the current
/// logical device of its device context, adapting the pool-size and sampler parameters to their Vulkan forms.
/// </summary>
/// <param name="deviceContext">The device context whose current logical device every call reaches; a device recreated
/// after a loss is picked up by the next call.</param>
/// <param name="allocator">The Vulkan descriptor allocator.</param>
/// <param name="naming">The naming every created pool and set is handed to.</param>
public sealed class VulkanGpuBindings(IVulkanDeviceContext deviceContext, VulkanDescriptorAllocator allocator, GpuObjectNaming naming) : IGpuBindings {
    private VulkanDeviceCommands Device => deviceContext.LogicalDevice.Commands;

    // One pool size per descriptor type the pool holds any of.
    private static ReadOnlyMemory<VulkanDescriptorPoolSize> BuildPoolSizes(in GpuDescriptorPoolSizes sizes) {
        var poolSizes = new List<VulkanDescriptorPoolSize>(capacity: 5);

        foreach (var (count, type) in ((ReadOnlySpan<(uint, uint)>)[
            (sizes.StorageBufferCount, VulkanDescriptorType.StorageBuffer),
            (sizes.StorageImageCount, VulkanDescriptorType.StorageImage),
            (sizes.ConstantBufferCount, VulkanDescriptorType.UniformBuffer),
            (sizes.SampledImageCount, VulkanDescriptorType.SampledImage),
            (sizes.SamplerCount, VulkanDescriptorType.Sampler),
        ])) {
            if (count > 0) {
                poolSizes.Add(item: new VulkanDescriptorPoolSize(
                    DescriptorCount: count,
                    DescriptorType: type
                ));
            }
        }

        return poolSizes.ToArray();
    }

    /// <inheritdoc/>
    /// <remarks>Zero: each pool is a <c>VkDescriptorPool</c> of its own, never refused by a shared heap.</remarks>
    public long HeapReleaseRevision => 0L;

    /// <inheritdoc/>
    /// <remarks>A set of a group's layout is recorded under that group in the device's
    /// <see cref="VulkanLogicalDevice.SetGroups"/>, which a <c>VkDescriptorSet</c> handle cannot carry, until its pool is
    /// destroyed.</remarks>
    public nint AllocateSet(nint poolHandle, nint descriptorSetLayoutHandle, in GpuObjectName name) {
        var logicalDevice = deviceContext.LogicalDevice;
        var set = allocator.AllocateSet(
            descriptorSetLayoutHandle: descriptorSetLayoutHandle,
            device: logicalDevice.Commands,
            poolHandle: poolHandle
        );

        logicalDevice.SetGroups.AddSet(
            poolHandle: poolHandle,
            setHandle: set,
            setLayoutHandle: descriptorSetLayoutHandle
        );
        naming.Name(
            handle: set,
            kind: GpuObjectKind.DescriptorSet,
            name: in name
        );

        return set;
    }
    /// <inheritdoc/>
    /// <remarks>Every candidate fits: each pool is a <c>VkDescriptorPool</c> of its own, sized by its creation.</remarks>
    public bool CanAdmit(string owner, IReadOnlyList<GpuDescriptorPoolSizes> pools, out string refusal) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: owner);
        ArgumentNullException.ThrowIfNull(argument: pools);

        refusal = string.Empty;

        return true;
    }
    /// <inheritdoc/>
    public nint CreatePool(in GpuDescriptorPoolSizes sizes, in GpuObjectName name) {
        var poolSizes = BuildPoolSizes(sizes: in sizes);
        var pool = allocator.CreatePool(
            device: Device,
            maxSets: sizes.MaxSets,
            poolSizes: poolSizes
        );

        naming.Name(
            handle: pool,
            kind: GpuObjectKind.DescriptorPool,
            name: in name
        );

        return pool;
    }
    /// <inheritdoc/>
    public nint CreateSampler(GpuSamplerFilter filter = GpuSamplerFilter.Linear) {
        var vulkanFilter = ((filter == GpuSamplerFilter.Nearest)
            ? VulkanFilter.Nearest
            : VulkanFilter.Linear
        );

        return allocator.CreateSampler(request: new VulkanSamplerCreateRequest(
            AddressModeU: VulkanSamplerAddressMode.ClampToEdge,
            AddressModeV: VulkanSamplerAddressMode.ClampToEdge,
            AddressModeW: VulkanSamplerAddressMode.ClampToEdge,
            AnisotropyEnable: 0,
            BorderColor: 0,
            CompareEnable: 0,
            CompareOp: 0,
            Device: Device,
            Flags: 0,
            MagFilter: vulkanFilter,
            MaxAnisotropy: 1f,
            // VK_LOD_CLAMP_NONE: every level a view covers is reachable, as Direct3D 12's MaxLOD of FLT_MAX leaves it.
            MaxLod: 1000f,
            MinFilter: vulkanFilter,
            MinLod: 0f,
            MipLodBias: 0f,
            MipmapMode: VulkanSamplerMipmapMode.Nearest,
            UnnormalizedCoordinates: 0
        ));
    }
    /// <inheritdoc/>
    /// <remarks>Pool zero returns before reading the device context's logical device, which a device that never
    /// came up does not have.</remarks>
    public void DestroyPool(nint poolHandle) {
        if (0 == poolHandle) {
            return;
        }

        var logicalDevice = deviceContext.LogicalDevice;

        logicalDevice.SetGroups.RemovePool(poolHandle: poolHandle);
        allocator.DestroyPool(
            device: logicalDevice.Commands,
            poolHandle: poolHandle
        );
    }
    /// <inheritdoc/>
    public void DestroySampler(nint samplerHandle) =>
        allocator.DestroySampler(
            device: Device,
            samplerHandle: samplerHandle
        );
    /// <inheritdoc/>
    /// <remarks>Whether the buffer is read-only or read-write, and the element stride, describe only a Direct3D 12
    /// view; every Vulkan buffer write is the same storage-buffer descriptor.</remarks>
    public void WriteBuffer(nint descriptorSetHandle, uint binding, nint bufferHandle, ulong bufferSize, GpuBindingKind kind, uint elementStride) {
        if (kind is not (GpuBindingKind.ReadOnlyBuffer or GpuBindingKind.ReadWriteBuffer)) {
            throw new ArgumentOutOfRangeException(
                actualValue: kind,
                message: "A buffer write names a read-only or read-write buffer kind.",
                paramName: nameof(kind)
            );
        }

        allocator.WriteStorageBuffer(
            binding: binding,
            bufferHandle: bufferHandle,
            bufferSize: bufferSize,
            descriptorSetHandle: descriptorSetHandle,
            device: Device
        );
    }
    /// <inheritdoc/>
    public void WriteConstantBuffer(nint descriptorSetHandle, uint binding, uint arrayElement, nint bufferHandle, ulong bufferSize) {
        IGpuBindings.RequireConstantBufferSize(bufferSize: bufferSize);
        allocator.WriteUniformBuffer(
            arrayElement: arrayElement,
            binding: binding,
            bufferHandle: bufferHandle,
            bufferSize: bufferSize,
            descriptorSetHandle: descriptorSetHandle,
            device: Device
        );
    }
    /// <inheritdoc/>
    public void WriteSampledImage(nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle) =>
        allocator.WriteSampledImage(
            arrayElement: arrayElement,
            binding: binding,
            descriptorSetHandle: descriptorSetHandle,
            device: Device,
            imageViewHandle: imageViewHandle
        );
    /// <inheritdoc/>
    public void WriteSampler(nint descriptorSetHandle, uint binding, uint arrayElement, nint samplerHandle) =>
        allocator.WriteSampler(
            arrayElement: arrayElement,
            binding: binding,
            descriptorSetHandle: descriptorSetHandle,
            device: Device,
            samplerHandle: samplerHandle
        );
    /// <inheritdoc/>
    public void WriteStorageImage(nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle) =>
        allocator.WriteStorageImage(
            arrayElement: arrayElement,
            binding: binding,
            descriptorSetHandle: descriptorSetHandle,
            device: Device,
            imageViewHandle: imageViewHandle
        );
}
