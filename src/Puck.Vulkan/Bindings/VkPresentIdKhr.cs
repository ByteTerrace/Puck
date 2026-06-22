using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Associates an application-supplied present id with a present operation; chained into
/// <see cref="VkPresentInfoKhr.PNext"/> so a later <c>vkWaitForPresentKHR</c> can block until that present is displayed.
/// </summary>
/// <remarks>1:1 ABI mirror of VkPresentIdKHR (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field names.</remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkPresentIdKhr {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_PRESENT_ID_KHR</c>).</summary>
    public uint SType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>The number of entries in <see cref="PPresentIds"/> (one per presented swapchain).</summary>
    public uint SwapchainCount;
    /// <summary>A pointer to an array of <c>uint64_t</c> present ids, one per swapchain.</summary>
    public nint PPresentIds;
}
