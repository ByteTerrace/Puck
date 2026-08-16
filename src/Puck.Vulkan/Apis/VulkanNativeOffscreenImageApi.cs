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
                MemoryTypeIndex = VulkanNativeBufferSupport.FindMemoryTypeIndex(
                    memoryProperties: in memoryProperties,
                    memoryTypeBits: memoryRequirements.MemoryTypeBits,
                    preferredProperties: MemoryPropertyDeviceLocalBit,
                    requireProperties: false,
                    resourceDescription: "an offscreen color image"
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
        return m_pointers.GetOrAdd(
            key: deviceHandle,
            valueFactory: static handle => new DevicePointers {
                CreateImage = ((delegate* unmanaged[Cdecl]<nint, in VkImageCreateInfo, nint, out nint, VkResult>)VulkanProcResolver.ResolveDeviceProc(deviceHandle: handle, functionName: "vkCreateImage"u8)),
                DestroyImage = ((delegate* unmanaged[Cdecl]<nint, nint, nint, void>)VulkanProcResolver.ResolveDeviceProc(deviceHandle: handle, functionName: "vkDestroyImage"u8)),
                GetImageMemoryRequirements = ((delegate* unmanaged[Cdecl]<nint, nint, out VkMemoryRequirements, void>)VulkanProcResolver.ResolveDeviceProc(deviceHandle: handle, functionName: "vkGetImageMemoryRequirements"u8)),
                AllocateMemory = ((delegate* unmanaged[Cdecl]<nint, in VkMemoryAllocateInfo, nint, out nint, VkResult>)VulkanProcResolver.ResolveDeviceProc(deviceHandle: handle, functionName: "vkAllocateMemory"u8)),
                FreeMemory = ((delegate* unmanaged[Cdecl]<nint, nint, nint, void>)VulkanProcResolver.ResolveDeviceProc(deviceHandle: handle, functionName: "vkFreeMemory"u8)),
                BindImageMemory = ((delegate* unmanaged[Cdecl]<nint, nint, nint, ulong, VkResult>)VulkanProcResolver.ResolveDeviceProc(deviceHandle: handle, functionName: "vkBindImageMemory"u8)),
            }
        );
    }
    private InstancePointers GetInstancePointers(nint instanceHandle) {
        return m_instancePointers.GetOrAdd(
            key: instanceHandle,
            valueFactory: static handle => new InstancePointers {
                GetPhysicalDeviceMemoryProperties = ((delegate* unmanaged[Cdecl]<nint, out VkPhysicalDeviceMemoryProperties, void>)VulkanProcResolver.ResolveInstanceProc(functionName: "vkGetPhysicalDeviceMemoryProperties"u8, instanceHandle: handle)),
            }
        );
    }
    private static void ValidateCreateRequest(VulkanOffscreenImageCreateRequest request) {
        VulkanArgument.RequireHandle(
            handle: request.DeviceHandle,
            handleDescription: "logical-device",
            paramName: nameof(request)
        );

        VulkanArgument.RequireHandle(
            handle: request.InstanceHandle,
            handleDescription: "instance",
            paramName: nameof(request)
        );

        VulkanArgument.RequireHandle(
            handle: request.PhysicalDeviceHandle,
            handleDescription: "physical-device",
            paramName: nameof(request)
        );

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
