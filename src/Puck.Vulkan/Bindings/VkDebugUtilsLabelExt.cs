using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// A named, optionally colored command-buffer label opened with <c>vkCmdBeginDebugUtilsLabelEXT</c> — a debug-marker
/// group GPU capture tools (RenderDoc / PIX / Nsight) render as a scope around the commands it encloses.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkDebugUtilsLabelEXT (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct VkDebugUtilsLabelExt {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_DEBUG_UTILS_LABEL_EXT</c>).</summary>
    public uint StructureType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint Next;
    /// <summary>A null-terminated UTF-8 string naming the label.</summary>
    public byte* LabelName;
    /// <summary>An optional RGBA color for the label (all-zero leaves it uncolored).</summary>
    public fixed float Color[4];
}
