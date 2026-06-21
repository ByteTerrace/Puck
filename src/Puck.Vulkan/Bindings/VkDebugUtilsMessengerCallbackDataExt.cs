using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// The data the validation layer passes to a debug-utils messenger callback for one message. Only the leading
/// fields up to <see cref="Message"/> are mirrored — the callback reads the human-readable message and ignores
/// the trailing object/label arrays — so the prefix layout matches the native structure exactly.
/// </summary>
/// <remarks>
/// ABI-prefix mirror of VkDebugUtilsMessengerCallbackDataEXT (vulkan_core.h, SDK 1.4): the fields through
/// <c>pMessage</c> are byte-identical to the native layout (the struct is only ever read through a pointer the
/// loader supplies, never allocated managed-side).
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkDebugUtilsMessengerCallbackDataExt {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value.</summary>
    public uint StructureType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint Next;
    /// <summary>A bitmask of <c>VkDebugUtilsMessengerCallbackDataFlagsEXT</c> (reserved; zero).</summary>
    public uint Flags;
    /// <summary>A null-terminated UTF-8 string naming the triggering message id, or <see langword="null"/>.</summary>
    public nint MessageIdName;
    /// <summary>The numeric id of the triggering message.</summary>
    public int MessageIdNumber;
    /// <summary>A null-terminated UTF-8 string with the human-readable message.</summary>
    public nint Message;
}
