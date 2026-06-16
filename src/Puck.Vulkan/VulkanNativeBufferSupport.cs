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

    /// <summary>Validates that the device, instance, and physical-device handles required to create a buffer are all non-zero.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <param name="instanceHandle">The native <c>VkInstance</c> handle.</param>
    /// <param name="physicalDeviceHandle">The native <c>VkPhysicalDevice</c> handle.</param>
    /// <param name="argumentName">The parameter name reported by a thrown exception.</param>
    /// <exception cref="ArgumentException">Any of the supplied handles is zero.</exception>
    public static void ValidateBufferHandles(nint deviceHandle, nint instanceHandle, nint physicalDeviceHandle, string argumentName) {
        if (deviceHandle == 0) {
            throw new ArgumentException(
                message: "Vulkan logical-device handle must be non-zero.",
                paramName: argumentName
            );
        }

        if (instanceHandle == 0) {
            throw new ArgumentException(
                message: "Vulkan instance handle must be non-zero.",
                paramName: argumentName
            );
        }

        if (physicalDeviceHandle == 0) {
            throw new ArgumentException(
                message: "Vulkan physical-device handle must be non-zero.",
                paramName: argumentName
            );
        }
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
