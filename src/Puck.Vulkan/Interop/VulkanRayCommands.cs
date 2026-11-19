using Puck.Vulkan.Bindings;

namespace Puck.Vulkan.Interop;

/// <summary>The acceleration-structure entry points of one logical device created with ray queries
/// (<see cref="GpuOptionalCapabilities.RayQuery"/>), resolved once with its command table, and the core
/// <c>vkGetBufferDeviceAddress</c> a build reads its inputs through.</summary>
public sealed unsafe class VulkanRayCommands {
    /// <summary>The <c>vkCreateAccelerationStructureKHR</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, in VkAccelerationStructureCreateInfoKhr, nint, out nint, VkResult> CreateAccelerationStructure;
    /// <summary>The <c>vkDestroyAccelerationStructureKHR</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, nint, nint, void> DestroyAccelerationStructure;
    /// <summary>The <c>vkGetAccelerationStructureBuildSizesKHR</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, uint, in VkAccelerationStructureBuildGeometryInfoKhr, in uint, ref VkAccelerationStructureBuildSizesInfoKhr, void> GetBuildSizes;
    /// <summary>The <c>vkGetAccelerationStructureDeviceAddressKHR</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, in VkHandleAddressInfo, ulong> GetAccelerationStructureAddress;
    /// <summary>The <c>vkGetBufferDeviceAddress</c> entry point (Vulkan 1.2 core).</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, in VkHandleAddressInfo, ulong> GetBufferAddress;
    /// <summary>The <c>vkCmdBuildAccelerationStructuresKHR</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, uint, in VkAccelerationStructureBuildGeometryInfoKhr, in nint, void> CmdBuild;
    /// <summary>The <c>vkCmdWriteAccelerationStructuresPropertiesKHR</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, uint, in nint, uint, nint, uint, void> CmdWriteProperties;
    /// <summary>The <c>vkCmdCopyAccelerationStructureKHR</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, in VkCopyAccelerationStructureInfoKhr, void> CmdCopy;

    /// <summary>Resolves the entry points from a device created with <c>VK_KHR_acceleration_structure</c>.</summary>
    /// <param name="deviceHandle">The device.</param>
    /// <param name="procedures">The device procedure resolver.</param>
    /// <exception cref="InvalidOperationException">The device does not expose one of them.</exception>
    public VulkanRayCommands(nint deviceHandle, VulkanProcResolver procedures) {
        ArgumentNullException.ThrowIfNull(argument: procedures);

        CreateAccelerationStructure = ((delegate* unmanaged[Cdecl]<nint, in VkAccelerationStructureCreateInfoKhr, nint, out nint, VkResult>)procedures.ResolveDeviceProc(deviceHandle: deviceHandle, functionName: "vkCreateAccelerationStructureKHR"u8));
        DestroyAccelerationStructure = ((delegate* unmanaged[Cdecl]<nint, nint, nint, void>)procedures.ResolveDeviceProc(deviceHandle: deviceHandle, functionName: "vkDestroyAccelerationStructureKHR"u8));
        GetBuildSizes = ((delegate* unmanaged[Cdecl]<nint, uint, in VkAccelerationStructureBuildGeometryInfoKhr, in uint, ref VkAccelerationStructureBuildSizesInfoKhr, void>)procedures.ResolveDeviceProc(deviceHandle: deviceHandle, functionName: "vkGetAccelerationStructureBuildSizesKHR"u8));
        GetAccelerationStructureAddress = ((delegate* unmanaged[Cdecl]<nint, in VkHandleAddressInfo, ulong>)procedures.ResolveDeviceProc(deviceHandle: deviceHandle, functionName: "vkGetAccelerationStructureDeviceAddressKHR"u8));
        GetBufferAddress = ((delegate* unmanaged[Cdecl]<nint, in VkHandleAddressInfo, ulong>)procedures.ResolveDeviceProc(deviceHandle: deviceHandle, functionName: "vkGetBufferDeviceAddress"u8));
        CmdBuild = ((delegate* unmanaged[Cdecl]<nint, uint, in VkAccelerationStructureBuildGeometryInfoKhr, in nint, void>)procedures.ResolveDeviceProc(deviceHandle: deviceHandle, functionName: "vkCmdBuildAccelerationStructuresKHR"u8));
        CmdWriteProperties = ((delegate* unmanaged[Cdecl]<nint, uint, in nint, uint, nint, uint, void>)procedures.ResolveDeviceProc(deviceHandle: deviceHandle, functionName: "vkCmdWriteAccelerationStructuresPropertiesKHR"u8));
        CmdCopy = ((delegate* unmanaged[Cdecl]<nint, in VkCopyAccelerationStructureInfoKhr, void>)procedures.ResolveDeviceProc(deviceHandle: deviceHandle, functionName: "vkCmdCopyAccelerationStructureKHR"u8));
    }
}
