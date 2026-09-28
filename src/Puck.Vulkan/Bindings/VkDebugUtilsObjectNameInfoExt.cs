using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// The name <c>vkSetDebugUtilsObjectNameEXT</c> gives one object, which validation messages then print beside its
/// handle.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkDebugUtilsObjectNameInfoEXT (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field
/// names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct VkDebugUtilsObjectNameInfoExt {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value
    /// (<c>VK_STRUCTURE_TYPE_DEBUG_UTILS_OBJECT_NAME_INFO_EXT</c>).</summary>
    public uint StructureType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint Next;
    /// <summary>The object's <c>VkObjectType</c>.</summary>
    public uint ObjectType;
    /// <summary>The object's handle, widened to 64 bits.</summary>
    public ulong ObjectHandle;
    /// <summary>A null-terminated UTF-8 string naming the object.</summary>
    public byte* ObjectName;
}
