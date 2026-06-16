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

    private readonly Lock m_syncRoot = new();
    private unsafe delegate* unmanaged[Cdecl]<nint, byte*, nint> m_getDeviceProcAddr;

    /// <inheritdoc/>
    public VkResult CreateShaderModule(VulkanShaderModuleCreateRequest request, out nint moduleHandle) {
        if (0 == request.DeviceHandle) {
            throw new ArgumentException(
                message: "Vulkan logical-device handle must be non-zero.",
                paramName: nameof(request)
            );
        }

        var createShaderModule = GetPointers(deviceHandle: request.DeviceHandle).CreateShaderModule;

        var spirVBytes = request.SpirVBytes.ToArray();
        var codeHandle = GCHandle.Alloc(
            type: GCHandleType.Pinned,
            value: spirVBytes
        );

        try {
            var createInfo = new VkShaderModuleCreateInfo {
                CodeSize = (nuint)spirVBytes.Length,
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

    private unsafe DevicePointers GetPointers(nint deviceHandle) {
        if (m_pointers.TryGetValue(
            key: deviceHandle,
            value: out var pointers
        )) {
            return pointers;
        }
        var getAddr = GetDeviceProcAddr();
        DevicePointers pNew = default;

        fixed (byte* pName = "vkCreateShaderModule"u8) {
            pNew.CreateShaderModule = (delegate* unmanaged[Cdecl]<nint, in VkShaderModuleCreateInfo, nint, out nint, VkResult>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkDestroyShaderModule"u8) {
            pNew.DestroyShaderModule = (delegate* unmanaged[Cdecl]<nint, nint, nint, void>)getAddr(
                deviceHandle,
                pName
            );
        }
        m_pointers[deviceHandle] = pNew;
        return pNew;
    }
    private unsafe delegate* unmanaged[Cdecl]<nint, byte*, nint> GetDeviceProcAddr() {
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
