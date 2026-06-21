using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Parameters describing a debug-utils messenger to be created with <c>vkCreateDebugUtilsMessengerEXT</c> — the
/// severities and message types to report, and the callback that receives them.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkDebugUtilsMessengerCreateInfoEXT (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic
/// field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkDebugUtilsMessengerCreateInfoExt {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_DEBUG_UTILS_MESSENGER_CREATE_INFO_EXT</c>).</summary>
    public uint StructureType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint Next;
    /// <summary>A bitmask of <c>VkDebugUtilsMessengerCreateFlagBitsEXT</c> (reserved; zero).</summary>
    public uint Flags;
    /// <summary>A bitmask of <c>VkDebugUtilsMessageSeverityFlagBitsEXT</c> severities that trigger the callback.</summary>
    public uint MessageSeverity;
    /// <summary>A bitmask of <c>VkDebugUtilsMessageTypeFlagBitsEXT</c> message types that trigger the callback.</summary>
    public uint MessageType;
    /// <summary>A pointer to the <c>PFN_vkDebugUtilsMessengerCallbackEXT</c> callback invoked for each message.</summary>
    public nint UserCallback;
    /// <summary>An opaque pointer passed through to the callback, or <see langword="null"/>.</summary>
    public nint UserData;
}
