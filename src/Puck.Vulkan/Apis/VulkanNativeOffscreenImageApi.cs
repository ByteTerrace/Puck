using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan;

/// <summary>Native <see cref="IVulkanOffscreenImageApi"/>: a generic device-local 2D color
/// image plus bound memory, loaded through the same per-device proc-address pattern as the
/// other native resource APIs. Carries no policy — usage and format are the caller's.</summary>
public unsafe sealed class VulkanNativeOffscreenImageApi : IVulkanOffscreenImageApi {
    private const uint ImageLayoutUndefined = 0;
    private const uint ImageTiling2DOptimal = 0;
    private const uint ImageType2D = 1;
    private const uint MemoryPropertyDeviceLocalBit = 0x00000001;
    private const uint SampleCount1Bit = 1;
    private const uint SharingModeExclusive = 0;
    private const uint StructureTypeImageCreateInfo = 14;
    private const uint StructureTypeMemoryAllocateInfo = 5;

    private readonly Lock m_syncRoot = new();
    private delegate* unmanaged[Cdecl]<nint, byte*, nint> m_getDeviceProcAddr;
    private delegate* unmanaged[Cdecl]<nint, byte*, nint> m_getInstanceProcAddr;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<nint, DevicePointers> m_pointers = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<nint, InstancePointers> m_instancePointers = new();

    /// <inheritdoc/>
    public VulkanOffscreenImageCreateResult CreateColorImage(VulkanOffscreenImageCreateRequest request) {
        ValidateCreateRequest(request: request);

        var pointers = GetPointers(deviceHandle: request.DeviceHandle);
        var getPhysicalDeviceMemoryProperties = GetInstancePointers(instanceHandle: request.InstanceHandle).GetPhysicalDeviceMemoryProperties;

        nint imageHandle = 0;
        nint memoryHandle = 0;

        try {
            var createInfo = new VkImageCreateInfo {
                ArrayLayers = 1,
                Extent = new VkExtent3D(
                    depth: 1,
                    height: request.Height,
                    width: request.Width
                ),
                Format = request.Format,
                ImageType = ImageType2D,
                InitialLayout = ImageLayoutUndefined,
                MipLevels = 1,
                SType = StructureTypeImageCreateInfo,
                Samples = SampleCount1Bit,
                SharingMode = SharingModeExclusive,
                Tiling = ImageTiling2DOptimal,
                Usage = request.UsageFlags,
            };

            pointers.CreateImage(
                request.DeviceHandle,
                in createInfo,
                0,
                out imageHandle
            ).ThrowIfFailed(operation: "vkCreateImage");
            pointers.GetImageMemoryRequirements(
                request.DeviceHandle,
                imageHandle,
                out var memoryRequirements
            );
            getPhysicalDeviceMemoryProperties(
                request.PhysicalDeviceHandle,
                out var memoryProperties
            );
            var allocateInfo = new VkMemoryAllocateInfo {
                AllocationSize = memoryRequirements.Size,
                MemoryTypeIndex = FindDeviceLocalMemoryTypeIndex(
                    memoryProperties: memoryProperties,
                    memoryTypeBits: memoryRequirements.MemoryTypeBits
                ),
                SType = StructureTypeMemoryAllocateInfo,
            };

            pointers.AllocateMemory(
                request.DeviceHandle,
                in allocateInfo,
                0,
                out memoryHandle
            ).ThrowIfFailed(operation: "vkAllocateMemory");
            pointers.BindImageMemory(
                request.DeviceHandle,
                imageHandle,
                memoryHandle,
                0
            ).ThrowIfFailed(operation: "vkBindImageMemory");

            return new VulkanOffscreenImageCreateResult(
                ImageHandle: imageHandle,
                MemoryHandle: memoryHandle
            );
        } catch {
            if (0 != imageHandle) {
                pointers.DestroyImage(
                    request.DeviceHandle,
                    imageHandle,
                    0
                );
            }

            if (0 != memoryHandle) {
                pointers.FreeMemory(
                    request.DeviceHandle,
                    memoryHandle,
                    0
                );
            }

            throw;
        }
    }
    /// <inheritdoc/>
    public void DestroyColorImage(nint deviceHandle, nint imageHandle, nint memoryHandle) {
        if (
            (0 == deviceHandle) ||
            ((0 == imageHandle) && (0 == memoryHandle))
        ) {
            return;
        }

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

    private struct DevicePointers {
        public delegate* unmanaged[Cdecl]<nint, in VkImageCreateInfo, nint, out nint, VkResult> CreateImage;
        public delegate* unmanaged[Cdecl]<nint, nint, nint, void> DestroyImage;
        public delegate* unmanaged[Cdecl]<nint, nint, out VkMemoryRequirements, void> GetImageMemoryRequirements;
        public delegate* unmanaged[Cdecl]<nint, in VkMemoryAllocateInfo, nint, out nint, VkResult> AllocateMemory;
        public delegate* unmanaged[Cdecl]<nint, nint, nint, void> FreeMemory;
        public delegate* unmanaged[Cdecl]<nint, nint, nint, ulong, VkResult> BindImageMemory;
    }
    private struct InstancePointers {
        public delegate* unmanaged[Cdecl]<nint, out VkPhysicalDeviceMemoryProperties, void> GetPhysicalDeviceMemoryProperties;
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

        fixed (byte* pName = "vkCreateImage"u8) {
            pNew.CreateImage = (delegate* unmanaged[Cdecl]<nint, in VkImageCreateInfo, nint, out nint, VkResult>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkDestroyImage"u8) {
            pNew.DestroyImage = (delegate* unmanaged[Cdecl]<nint, nint, nint, void>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkGetImageMemoryRequirements"u8) {
            pNew.GetImageMemoryRequirements = (delegate* unmanaged[Cdecl]<nint, nint, out VkMemoryRequirements, void>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkAllocateMemory"u8) {
            pNew.AllocateMemory = (delegate* unmanaged[Cdecl]<nint, in VkMemoryAllocateInfo, nint, out nint, VkResult>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkFreeMemory"u8) {
            pNew.FreeMemory = (delegate* unmanaged[Cdecl]<nint, nint, nint, void>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkBindImageMemory"u8) {
            pNew.BindImageMemory = (delegate* unmanaged[Cdecl]<nint, nint, nint, ulong, VkResult>)getAddr(
                deviceHandle,
                pName
            );
        }

        m_pointers[deviceHandle] = pNew;
        return pNew;
    }
    private InstancePointers GetInstancePointers(nint instanceHandle) {
        if (m_instancePointers.TryGetValue(
            key: instanceHandle,
            value: out var pointers
        )) {
            return pointers;
        }

        var getAddr = GetInstanceProcAddr();
        InstancePointers pNew = default;

        fixed (byte* pName = "vkGetPhysicalDeviceMemoryProperties"u8) {
            pNew.GetPhysicalDeviceMemoryProperties = (delegate* unmanaged[Cdecl]<nint, out VkPhysicalDeviceMemoryProperties, void>)getAddr(
                instanceHandle,
                pName
            );
        }

        m_instancePointers[instanceHandle] = pNew;
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
    private delegate* unmanaged[Cdecl]<nint, byte*, nint> GetInstanceProcAddr() {
        lock (m_syncRoot) {
            if (m_getInstanceProcAddr is not null) {
                return m_getInstanceProcAddr;
            }

            var export = VulkanNativeLibrary.GetExport(functionName: "vkGetInstanceProcAddr");

            m_getInstanceProcAddr = (delegate* unmanaged[Cdecl]<nint, byte*, nint>)export;
            return m_getInstanceProcAddr;
        }
    }
    private static uint FindDeviceLocalMemoryTypeIndex(uint memoryTypeBits, VkPhysicalDeviceMemoryProperties memoryProperties) {
        var fallbackIndex = -1;

        for (var index = 0; (index < memoryProperties.MemoryTypeCount); index++) {
            if (0 == (memoryTypeBits & (1u << index))) {
                continue;
            }

            if (0 != (memoryProperties.MemoryTypePropertyFlags(memoryTypeIndex: index) & MemoryPropertyDeviceLocalBit)) {
                return (uint)index;
            }

            if (0 > fallbackIndex) {
                fallbackIndex = index;
            }
        }

        if (0 <= fallbackIndex) {
            return (uint)fallbackIndex;
        }

        throw new InvalidOperationException(message: "The Vulkan physical device did not report a compatible memory type for an offscreen color image.");
    }
    private static void ValidateCreateRequest(VulkanOffscreenImageCreateRequest request) {
        if (0 == request.DeviceHandle) {
            throw new ArgumentException(
                message: "Vulkan logical-device handle must be non-zero.",
                paramName: nameof(request)
            );
        }

        if (0 == request.InstanceHandle) {
            throw new ArgumentException(
                message: "Vulkan instance handle must be non-zero.",
                paramName: nameof(request)
            );
        }

        if (0 == request.PhysicalDeviceHandle) {
            throw new ArgumentException(
                message: "Vulkan physical-device handle must be non-zero.",
                paramName: nameof(request)
            );
        }

        ArgumentOutOfRangeException.ThrowIfZero(
            value: request.Width,
            paramName: nameof(request)
        );
        ArgumentOutOfRangeException.ThrowIfZero(
            value: request.Height,
            paramName: nameof(request)
        );
    }
}
