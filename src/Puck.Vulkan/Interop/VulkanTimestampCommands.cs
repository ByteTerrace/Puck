using Puck.Vulkan.Bindings;

namespace Puck.Vulkan.Interop;

/// <summary>The timestamp entry points of one logical device, resolved once with its command table.</summary>
public sealed unsafe class VulkanTimestampCommands {
    /// <summary>The vkCreateQueryPool entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, in VkQueryPoolCreateInfo, nint, out nint, VkResult> CreateQueryPool;
    /// <summary>The vkDestroyQueryPool entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, nint, nint, void> DestroyQueryPool;
    /// <summary>The vkCmdResetQueryPool entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, nint, uint, uint, void> CmdResetQueryPool;
    /// <summary>The vkCmdWriteTimestamp entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, uint, nint, uint, void> CmdWriteTimestamp;
    /// <summary>The vkCmdCopyQueryPoolResults entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, nint, uint, uint, nint, ulong, ulong, uint, void> CmdCopyQueryPoolResults;

    /// <summary>Resolves the core timestamp commands from one device.</summary>
    /// <param name="deviceHandle">The device.</param>
    /// <param name="procedures">The device procedure resolver.</param>
    public VulkanTimestampCommands(nint deviceHandle, VulkanProcResolver procedures) {
        CreateQueryPool = ((delegate* unmanaged[Cdecl]<nint, in VkQueryPoolCreateInfo, nint, out nint, VkResult>)procedures.ResolveDeviceProc(deviceHandle: deviceHandle, functionName: "vkCreateQueryPool"u8));
        DestroyQueryPool = ((delegate* unmanaged[Cdecl]<nint, nint, nint, void>)procedures.ResolveDeviceProc(deviceHandle: deviceHandle, functionName: "vkDestroyQueryPool"u8));
        CmdResetQueryPool = ((delegate* unmanaged[Cdecl]<nint, nint, uint, uint, void>)procedures.ResolveDeviceProc(deviceHandle: deviceHandle, functionName: "vkCmdResetQueryPool"u8));
        CmdWriteTimestamp = ((delegate* unmanaged[Cdecl]<nint, uint, nint, uint, void>)procedures.ResolveDeviceProc(deviceHandle: deviceHandle, functionName: "vkCmdWriteTimestamp"u8));
        CmdCopyQueryPoolResults = ((delegate* unmanaged[Cdecl]<nint, nint, uint, uint, nint, ulong, ulong, uint, void>)procedures.ResolveDeviceProc(deviceHandle: deviceHandle, functionName: "vkCmdCopyQueryPoolResults"u8));
    }
}
