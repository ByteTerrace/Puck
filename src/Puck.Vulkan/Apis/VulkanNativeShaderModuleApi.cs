using System.Runtime.InteropServices;
using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan;

/// <summary>
/// The native implementation of <see cref="IVulkanShaderModuleApi"/>, marshaling to the
/// <c>vkCreateShaderModule</c> and <c>vkDestroyShaderModule</c> entry points resolved from the Vulkan loader.
/// </summary>
public unsafe sealed class VulkanNativeShaderModuleApi : IVulkanShaderModuleApi {
    private const uint StructureTypeShaderModuleCreateInfo = 16;

    /// <inheritdoc/>
    public VkResult CreateShaderModule(VulkanShaderModuleCreateRequest request, out nint moduleHandle) {
        VulkanArgument.RequireHandle(
            handle: request.DeviceHandle,
            handleDescription: "logical-device",
            paramName: nameof(request)
        );

        var createShaderModule = GetPointers(deviceHandle: request.DeviceHandle).CreateShaderModule;

        var spirVBytes = request.SpirVBytes.ToArray();
        var codeHandle = GCHandle.Alloc(
            type: GCHandleType.Pinned,
            value: spirVBytes
        );

        try {
            var createInfo = new VkShaderModuleCreateInfo {
                CodeSize = ((nuint)spirVBytes.Length),
                PCode = codeHandle.AddrOfPinnedObject(),
                SType = StructureTypeShaderModuleCreateInfo,
            };

            return createShaderModule(
                request.DeviceHandle,
                in createInfo,
                0,
                out moduleHandle
            );
        } finally {
            codeHandle.Free();
        }
    }
    /// <inheritdoc/>
    public void DestroyShaderModule(nint deviceHandle, nint moduleHandle) {
        if (
            (0 == deviceHandle) ||
            (0 == moduleHandle)
        ) {
            return;
        }

        var destroyShaderModule = GetPointers(deviceHandle: deviceHandle).DestroyShaderModule;

        destroyShaderModule(
            deviceHandle,
            moduleHandle,
            0
        );
    }

    private unsafe struct DevicePointers {
        public delegate* unmanaged[Cdecl]<nint, in VkShaderModuleCreateInfo, nint, out nint, VkResult> CreateShaderModule;
        public delegate* unmanaged[Cdecl]<nint, nint, nint, void> DestroyShaderModule;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<nint, DevicePointers> m_pointers = new();

    private DevicePointers GetPointers(nint deviceHandle) {
        return m_pointers.GetOrAdd(
            key: deviceHandle,
            valueFactory: static handle => new DevicePointers {
                CreateShaderModule = ((delegate* unmanaged[Cdecl]<nint, in VkShaderModuleCreateInfo, nint, out nint, VkResult>)VulkanProcResolver.ResolveDeviceProc(deviceHandle: handle, functionName: "vkCreateShaderModule"u8)),
                DestroyShaderModule = ((delegate* unmanaged[Cdecl]<nint, nint, nint, void>)VulkanProcResolver.ResolveDeviceProc(deviceHandle: handle, functionName: "vkDestroyShaderModule"u8)),
            }
        );
    }
}
