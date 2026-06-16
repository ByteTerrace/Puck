using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan;

/// <summary>
/// The native implementation of <see cref="IVulkanExternalMemoryApi"/>: it creates a Vulkan image flagged for
/// external memory, imports the shared NT handle's memory as a dedicated allocation, and binds them — letting a
/// texture another backend produced be sampled without a CPU round-trip.
/// </summary>
public unsafe sealed class VulkanNativeExternalMemoryApi : IVulkanExternalMemoryApi {
    // Direct3D 12 committed resources shared via CreateSharedHandle are imported through the D3D11-texture
    // handle type (they use the same kernel handle); the D3D12-resource bit is not a compatible import type for
    // a sampled color texture on the drivers tested.
    private const uint ExternalMemoryHandleTypeD3D11TextureBit = 0x00000010;
    private const uint ImageTiling2dOptimal = 0;
    private const uint ImageType2d = 1;
    // The shared resource is a Direct3D 12 render target, so the imported image must allow color-attachment use
    // (matching the producer) in addition to being sampled by the consumer.
    private const uint ImageUsageColorAttachmentBit = 0x00000010;
    private const uint ImageUsageSampledBit = 0x00000004;
    private const uint MemoryPropertyDeviceLocalBit = 0x00000001;
    private const uint SampleCount1Bit = 1;
    private const uint SharingModeExclusive = 0;
    private const uint StructureTypeExternalMemoryImageCreateInfo = 1000072001;
    private const uint StructureTypeImageCreateInfo = 14;
    private const uint StructureTypeImportMemoryWin32HandleInfo = 1000073000;
    private const uint StructureTypeMemoryAllocateInfo = 5;
    private const uint StructureTypeMemoryDedicatedAllocateInfo = 1000127001;
    private const uint StructureTypeMemoryWin32HandleProperties = 1000073002;

    private readonly Lock m_syncRoot = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<nint, DevicePointers> m_pointers = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<nint, InstancePointers> m_instancePointers = new();
    private delegate* unmanaged[Cdecl]<nint, byte*, nint> m_getDeviceProcAddr;
    private delegate* unmanaged[Cdecl]<nint, byte*, nint> m_getInstanceProcAddr;

    private struct DevicePointers {
        public delegate* unmanaged[Cdecl]<nint, in VkImageCreateInfo, nint, out nint, VkResult> CreateImage;
        public delegate* unmanaged[Cdecl]<nint, nint, nint, void> DestroyImage;
        public delegate* unmanaged[Cdecl]<nint, nint, out VkMemoryRequirements, void> GetImageMemoryRequirements;
        public delegate* unmanaged[Cdecl]<nint, in VkMemoryAllocateInfo, nint, out nint, VkResult> AllocateMemory;
        public delegate* unmanaged[Cdecl]<nint, nint, nint, void> FreeMemory;
        public delegate* unmanaged[Cdecl]<nint, nint, nint, ulong, VkResult> BindImageMemory;
        public delegate* unmanaged[Cdecl]<nint, uint, nint, out VkMemoryWin32HandlePropertiesKHR, VkResult> GetMemoryWin32HandleProperties;
    }
    private struct InstancePointers {
        public delegate* unmanaged[Cdecl]<nint, out VkPhysicalDeviceMemoryProperties, void> GetPhysicalDeviceMemoryProperties;
    }

    /// <inheritdoc/>
    public VulkanExternalImageImportResult ImportImage(VulkanExternalImageImportRequest request) {
        var pointers = GetPointers(deviceHandle: request.DeviceHandle);

        var externalInfo = new VkExternalMemoryImageCreateInfo {
            HandleTypes = ExternalMemoryHandleTypeD3D11TextureBit,
            SType = StructureTypeExternalMemoryImageCreateInfo,
        };
        var imageInfo = new VkImageCreateInfo {
            ArrayLayers = 1,
            Extent = new VkExtent3D(
                width: request.Width,
                height: request.Height,
                depth: 1
            ),
            Format = request.Format,
            ImageType = ImageType2d,
            InitialLayout = 0,
            MipLevels = 1,
            PNext = (nint)(&externalInfo),
            SType = StructureTypeImageCreateInfo,
            Samples = SampleCount1Bit,
            SharingMode = SharingModeExclusive,
            Tiling = ImageTiling2dOptimal,
            Usage = ImageUsageSampledBit | ImageUsageColorAttachmentBit,
        };

        pointers.CreateImage(
            request.DeviceHandle,
            in imageInfo,
            0,
            out var imageHandle
        ).ThrowIfFailed(operation: "vkCreateImage");

        try {
            pointers.GetImageMemoryRequirements(
                request.DeviceHandle,
                imageHandle,
                out var memoryRequirements
            );

            var handleProperties = new VkMemoryWin32HandlePropertiesKHR {
                SType = StructureTypeMemoryWin32HandleProperties,
            };

            pointers.GetMemoryWin32HandleProperties(
                request.DeviceHandle,
                ExternalMemoryHandleTypeD3D11TextureBit,
                request.SharedHandle,
                out handleProperties
            ).ThrowIfFailed(operation: "vkGetMemoryWin32HandlePropertiesKHR");

            GetInstancePointers(instanceHandle: request.InstanceHandle).GetPhysicalDeviceMemoryProperties(
                request.PhysicalDeviceHandle,
                out var memoryProperties
            );

            var memoryTypeIndex = FindMemoryTypeIndex(
                memoryProperties: memoryProperties,
                memoryTypeBits: memoryRequirements.MemoryTypeBits & handleProperties.MemoryTypeBits
            );
            var dedicatedInfo = new VkMemoryDedicatedAllocateInfo {
                Image = imageHandle,
                SType = StructureTypeMemoryDedicatedAllocateInfo,
            };
            var importInfo = new VkImportMemoryWin32HandleInfoKHR {
                Handle = request.SharedHandle,
                HandleType = ExternalMemoryHandleTypeD3D11TextureBit,
                PNext = (nint)(&dedicatedInfo),
                SType = StructureTypeImportMemoryWin32HandleInfo,
            };
            var allocateInfo = new VkMemoryAllocateInfo {
                AllocationSize = memoryRequirements.Size,
                MemoryTypeIndex = memoryTypeIndex,
                PNext = (nint)(&importInfo),
                SType = StructureTypeMemoryAllocateInfo,
            };

            pointers.AllocateMemory(
                request.DeviceHandle,
                in allocateInfo,
                0,
                out var memoryHandle
            ).ThrowIfFailed(operation: "vkAllocateMemory");

            try {
                pointers.BindImageMemory(
                    request.DeviceHandle,
                    imageHandle,
                    memoryHandle,
                    0
                ).ThrowIfFailed(operation: "vkBindImageMemory");

                return new VulkanExternalImageImportResult(
                    ImageHandle: imageHandle,
                    MemoryHandle: memoryHandle
                );
            } catch {
                pointers.FreeMemory(
                    request.DeviceHandle,
                    memoryHandle,
                    0
                );

                throw;
            }
        } catch {
            pointers.DestroyImage(
                request.DeviceHandle,
                imageHandle,
                0
            );

            throw;
        }
    }
    /// <inheritdoc/>
    public void DestroyImage(nint deviceHandle, nint imageHandle, nint memoryHandle) {
        var pointers = GetPointers(deviceHandle: deviceHandle);

        if (0 != imageHandle) {
            pointers.DestroyImage(
                deviceHandle,
                imageHandle,
                0
            );
        }

        if (0 != memoryHandle) {
            pointers.FreeMemory(
                deviceHandle,
                memoryHandle,
                0
            );
        }
    }

    private static uint FindMemoryTypeIndex(VkPhysicalDeviceMemoryProperties memoryProperties, uint memoryTypeBits) {
        for (var index = 0; (index < memoryProperties.MemoryTypeCount); index++) {
            var supported = (0 != (memoryTypeBits & (1u << index)));
            var deviceLocal = (0 != (memoryProperties.MemoryTypePropertyFlags(memoryTypeIndex: index) & MemoryPropertyDeviceLocalBit));

            if (
                supported &&
                deviceLocal
            ) {
                return (uint)index;
            }
        }

        for (var index = 0; (index < memoryProperties.MemoryTypeCount); index++) {
            if (0 != (memoryTypeBits & (1u << index))) {
                return (uint)index;
            }
        }

        throw new InvalidOperationException(message: "No Vulkan memory type is compatible with the imported external handle.");
    }
    private DevicePointers GetPointers(nint deviceHandle) {
        if (m_pointers.TryGetValue(
            key: deviceHandle,
            value: out var existing
        )) {
            return existing;
        }

        var getAddr = GetDeviceProcAddr();
        DevicePointers pointers = default;

        fixed (byte* name = "vkCreateImage"u8) {
            pointers.CreateImage = (delegate* unmanaged[Cdecl]<nint, in VkImageCreateInfo, nint, out nint, VkResult>)getAddr(
                deviceHandle,
                name
            );
        }
        fixed (byte* name = "vkDestroyImage"u8) {
            pointers.DestroyImage = (delegate* unmanaged[Cdecl]<nint, nint, nint, void>)getAddr(
                deviceHandle,
                name
            );
        }
        fixed (byte* name = "vkGetImageMemoryRequirements"u8) {
            pointers.GetImageMemoryRequirements = (delegate* unmanaged[Cdecl]<nint, nint, out VkMemoryRequirements, void>)getAddr(
                deviceHandle,
                name
            );
        }
        fixed (byte* name = "vkAllocateMemory"u8) {
            pointers.AllocateMemory = (delegate* unmanaged[Cdecl]<nint, in VkMemoryAllocateInfo, nint, out nint, VkResult>)getAddr(
                deviceHandle,
                name
            );
        }
        fixed (byte* name = "vkFreeMemory"u8) {
            pointers.FreeMemory = (delegate* unmanaged[Cdecl]<nint, nint, nint, void>)getAddr(
                deviceHandle,
                name
            );
        }
        fixed (byte* name = "vkBindImageMemory"u8) {
            pointers.BindImageMemory = (delegate* unmanaged[Cdecl]<nint, nint, nint, ulong, VkResult>)getAddr(
                deviceHandle,
                name
            );
        }
        fixed (byte* name = "vkGetMemoryWin32HandlePropertiesKHR"u8) {
            pointers.GetMemoryWin32HandleProperties = (delegate* unmanaged[Cdecl]<nint, uint, nint, out VkMemoryWin32HandlePropertiesKHR, VkResult>)getAddr(
                deviceHandle,
                name
            );
        }

        m_pointers[deviceHandle] = pointers;

        return pointers;
    }
    private InstancePointers GetInstancePointers(nint instanceHandle) {
        if (m_instancePointers.TryGetValue(
            key: instanceHandle,
            value: out var existing
        )) {
            return existing;
        }

        var getAddr = GetInstanceProcAddr();
        InstancePointers pointers = default;

        fixed (byte* name = "vkGetPhysicalDeviceMemoryProperties"u8) {
            pointers.GetPhysicalDeviceMemoryProperties = (delegate* unmanaged[Cdecl]<nint, out VkPhysicalDeviceMemoryProperties, void>)getAddr(
                instanceHandle,
                name
            );
        }

        m_instancePointers[instanceHandle] = pointers;

        return pointers;
    }
    private delegate* unmanaged[Cdecl]<nint, byte*, nint> GetDeviceProcAddr() {
        lock (m_syncRoot) {
            if (m_getDeviceProcAddr is null) {
                m_getDeviceProcAddr = (delegate* unmanaged[Cdecl]<nint, byte*, nint>)VulkanNativeLibrary.GetExport(functionName: "vkGetDeviceProcAddr");
            }

            return m_getDeviceProcAddr;
        }
    }
    private delegate* unmanaged[Cdecl]<nint, byte*, nint> GetInstanceProcAddr() {
        lock (m_syncRoot) {
            if (m_getInstanceProcAddr is null) {
                m_getInstanceProcAddr = (delegate* unmanaged[Cdecl]<nint, byte*, nint>)VulkanNativeLibrary.GetExport(functionName: "vkGetInstanceProcAddr");
            }

            return m_getInstanceProcAddr;
        }
    }
}
