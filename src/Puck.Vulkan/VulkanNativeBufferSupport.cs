using System.Runtime.InteropServices;
using Puck.Vulkan.Bindings;

namespace Puck.Vulkan;

/// <summary>
/// Shared native plumbing for the buffer-related APIs: handle validation, exclusive buffer creation, and
/// host-visible data upload through raw <c>vkXxx</c> function pointers.
/// </summary>
public static unsafe class VulkanNativeBufferSupport {
    private const uint SharingModeExclusive = 0;
    private const uint StructureTypeBufferCreateInfo = 12;
    private const uint StructureTypeMemoryAllocateInfo = 5;

    /// <summary>Validates that the device, instance, and physical-device handles required to create a buffer are all non-zero.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <param name="instanceHandle">The native <c>VkInstance</c> handle.</param>
    /// <param name="physicalDeviceHandle">The native <c>VkPhysicalDevice</c> handle.</param>
    /// <param name="argumentName">The parameter name reported by a thrown exception.</param>
    /// <exception cref="ArgumentException">Any of the supplied handles is zero.</exception>
    public static void ValidateBufferHandles(nint deviceHandle, nint instanceHandle, nint physicalDeviceHandle, string argumentName) {
        VulkanArgument.RequireHandle(
            handle: deviceHandle,
            handleDescription: "logical-device",
            paramName: argumentName
        );
        VulkanArgument.RequireHandle(
            handle: instanceHandle,
            handleDescription: "instance",
            paramName: argumentName
        );
        VulkanArgument.RequireHandle(
            handle: physicalDeviceHandle,
            handleDescription: "physical-device",
            paramName: argumentName
        );
    }
    /// <summary>Creates a buffer with exclusive sharing mode using the supplied native <c>vkCreateBuffer</c> entry point.</summary>
    /// <param name="createBuffer">A pointer to the native <c>vkCreateBuffer</c> entry point.</param>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <param name="size">The size, in bytes, of the buffer.</param>
    /// <param name="usage">A bitmask of <c>VkBufferUsageFlagBits</c> describing the intended usage of the buffer.</param>
    /// <returns>The created native <c>VkBuffer</c> handle.</returns>
    /// <exception cref="VulkanException">The native buffer creation call failed.</exception>
    public static nint CreateBuffer(delegate* unmanaged[Cdecl]<nint, in VkBufferCreateInfo, nint, out nint, VkResult> createBuffer, nint deviceHandle, ulong size, uint usage) {
        var createInfo = new VkBufferCreateInfo {
            SType = StructureTypeBufferCreateInfo,
            SharingMode = SharingModeExclusive,
            Size = size,
            Usage = usage,
        };
        var result = createBuffer(
            deviceHandle,
            in createInfo,
            0,
            out var bufferHandle
        );

        result.ThrowIfFailed(operation: "vkCreateBuffer");
        return bufferHandle;
    }
    /// <summary>Selects a device memory-type index compatible with a resource's requirements and desired properties.</summary>
    /// <param name="memoryProperties">The physical device's reported memory types.</param>
    /// <param name="memoryTypeBits">The bitmask of memory types the resource's requirements permit.</param>
    /// <param name="preferredProperties">A bitmask of <c>VkMemoryPropertyFlagBits</c> a selected type should carry in full.</param>
    /// <param name="requireProperties"><see langword="true"/> to fail when no permitted type carries every preferred property; <see langword="false"/> to fall back to the first permitted type.</param>
    /// <param name="resourceDescription">A short noun phrase naming the resource, used in the failure message.</param>
    /// <returns>The index of a compatible memory type.</returns>
    /// <exception cref="InvalidOperationException">No permitted memory type carries the preferred properties and either <paramref name="requireProperties"/> is set or none is permitted at all.</exception>
    public static uint FindMemoryTypeIndex(
        in VkPhysicalDeviceMemoryProperties memoryProperties,
        uint memoryTypeBits,
        uint preferredProperties,
        bool requireProperties,
        string resourceDescription
    ) {
        var fallbackIndex = -1;

        for (var index = 0; (index < memoryProperties.MemoryTypeCount); index++) {
            if (0 == (memoryTypeBits & (1u << index))) {
                continue;
            }

            if ((memoryProperties.MemoryTypePropertyFlags(memoryTypeIndex: index) & preferredProperties) == preferredProperties) {
                return ((uint)index);
            }

            if (fallbackIndex < 0) {
                fallbackIndex = index;
            }
        }

        if (
            requireProperties ||
            (fallbackIndex < 0)
        ) {
            throw new InvalidOperationException(message: $"The Vulkan physical device did not report a compatible memory type for {resourceDescription}.");
        }

        return ((uint)fallbackIndex);
    }
    /// <summary>Allocates device memory sized and typed for a buffer and binds it, freeing the allocation if the bind fails.</summary>
    /// <param name="allocateMemory">A pointer to the native <c>vkAllocateMemory</c> entry point.</param>
    /// <param name="bindBufferMemory">A pointer to the native <c>vkBindBufferMemory</c> entry point.</param>
    /// <param name="freeMemory">A pointer to the native <c>vkFreeMemory</c> entry point.</param>
    /// <param name="getBufferMemoryRequirements">A pointer to the native <c>vkGetBufferMemoryRequirements</c> entry point.</param>
    /// <param name="getPhysicalDeviceMemoryProperties">A pointer to the native <c>vkGetPhysicalDeviceMemoryProperties</c> entry point.</param>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <param name="physicalDeviceHandle">The native <c>VkPhysicalDevice</c> handle whose memory types are queried.</param>
    /// <param name="bufferHandle">The native <c>VkBuffer</c> handle the allocation is bound to.</param>
    /// <param name="preferredProperties">A bitmask of <c>VkMemoryPropertyFlagBits</c> the allocation's memory type should carry.</param>
    /// <param name="requireProperties"><see langword="true"/> to fail rather than fall back when no type carries the preferred properties.</param>
    /// <param name="resourceDescription">A short noun phrase naming the resource, used in the memory-type failure message.</param>
    /// <returns>The bound native <c>VkDeviceMemory</c> handle.</returns>
    /// <exception cref="VulkanException">A native allocation or bind call failed.</exception>
    /// <exception cref="InvalidOperationException">No compatible memory type was found.</exception>
    public static nint AllocateAndBindMemory(
        delegate* unmanaged[Cdecl]<nint, in VkMemoryAllocateInfo, nint, out nint, VkResult> allocateMemory,
        delegate* unmanaged[Cdecl]<nint, nint, nint, ulong, VkResult> bindBufferMemory,
        delegate* unmanaged[Cdecl]<nint, nint, nint, void> freeMemory,
        delegate* unmanaged[Cdecl]<nint, nint, out VkMemoryRequirements, void> getBufferMemoryRequirements,
        delegate* unmanaged[Cdecl]<nint, out VkPhysicalDeviceMemoryProperties, void> getPhysicalDeviceMemoryProperties,
        nint deviceHandle,
        nint physicalDeviceHandle,
        nint bufferHandle,
        uint preferredProperties,
        bool requireProperties,
        string resourceDescription
    ) {
        getBufferMemoryRequirements(
            deviceHandle,
            bufferHandle,
            out var memoryRequirements
        );
        getPhysicalDeviceMemoryProperties(
            physicalDeviceHandle,
            out var memoryProperties
        );
        var allocateInfo = new VkMemoryAllocateInfo {
            AllocationSize = memoryRequirements.Size,
            MemoryTypeIndex = FindMemoryTypeIndex(
                memoryProperties: in memoryProperties,
                memoryTypeBits: memoryRequirements.MemoryTypeBits,
                preferredProperties: preferredProperties,
                requireProperties: requireProperties,
                resourceDescription: resourceDescription
            ),
            SType = StructureTypeMemoryAllocateInfo,
        };

        allocateMemory(
            deviceHandle,
            in allocateInfo,
            0,
            out var memoryHandle
        ).ThrowIfFailed(operation: "vkAllocateMemory");

        try {
            bindBufferMemory(
                deviceHandle,
                bufferHandle,
                memoryHandle,
                0
            ).ThrowIfFailed(operation: "vkBindBufferMemory");
            return memoryHandle;
        } catch {
            freeMemory(
                deviceHandle,
                memoryHandle,
                0
            );
            throw;
        }
    }
    /// <summary>Uploads data into host-visible device memory by mapping it, copying the bytes, and unmapping it, using the supplied native entry points.</summary>
    /// <param name="mapMemory">A pointer to the native <c>vkMapMemory</c> entry point.</param>
    /// <param name="unmapMemory">A pointer to the native <c>vkUnmapMemory</c> entry point.</param>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <param name="memoryHandle">The native <c>VkDeviceMemory</c> handle to upload into.</param>
    /// <param name="data">The data to copy into the memory, starting at offset zero.</param>
    /// <exception cref="VulkanException">The native memory mapping call failed.</exception>
    public static void UploadBufferData(
        delegate* unmanaged[Cdecl]<nint, nint, ulong, nuint, uint, out nint, VkResult> mapMemory,
        delegate* unmanaged[Cdecl]<nint, nint, void> unmapMemory,
        nint deviceHandle,
        nint memoryHandle,
        byte[] data
    ) {
        var mapResult = mapMemory(
            deviceHandle,
            memoryHandle,
            0,
            checked((nuint)data.Length),
            0,
            out var mappedMemory
        );

        mapResult.ThrowIfFailed(operation: "vkMapMemory");

        try {
            Marshal.Copy(
                destination: mappedMemory,
                length: data.Length,
                source: data,
                startIndex: 0
            );
        } finally {
            unmapMemory(
                deviceHandle,
                memoryHandle
            );
        }
    }
}
