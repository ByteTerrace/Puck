using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Describes a vertex buffer binding: its byte stride and whether its attributes advance per vertex or per instance.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkVertexInputBindingDescription (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic
/// field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkVertexInputBindingDescription {
    /// <summary>The binding number that this structure describes.</summary>
    public uint Binding;
    /// <summary>The byte distance between consecutive elements within the buffer.</summary>
    public uint Stride;
    /// <summary>Whether attribute addressing advances per vertex or per instance, as a <c>VkVertexInputRate</c> value.</summary>
    public uint InputRate;
}
