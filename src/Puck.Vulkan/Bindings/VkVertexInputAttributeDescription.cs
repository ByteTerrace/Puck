using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Describes a single vertex input attribute: the shader location it feeds, the binding it is sourced
/// from, and its format and byte offset within a vertex element.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkVertexInputAttributeDescription (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic
/// field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkVertexInputAttributeDescription {
    /// <summary>The shader input location that this attribute feeds.</summary>
    public uint Location;
    /// <summary>The binding number from which this attribute takes its data.</summary>
    public uint Binding;
    /// <summary>The size and type of the attribute data, as a <c>VkFormat</c> value.</summary>
    public uint Format;
    /// <summary>The byte offset of this attribute relative to the start of an element in the vertex buffer binding.</summary>
    public uint Offset;
}
