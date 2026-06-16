namespace Puck.Vulkan;

/// <summary>
/// Common <c>VkShaderStageFlagBits</c> values used when declaring push constant ranges and descriptor bindings.
/// </summary>
public static class VulkanShaderStageFlags {
    /// <summary>The fragment shader stage bit.</summary>
    public const uint Fragment = 0x00000010;
    /// <summary>The vertex shader stage bit.</summary>
    public const uint Vertex = 0x00000001;
    /// <summary>The combined vertex and fragment shader stage bits.</summary>
    public const uint VertexAndFragment = Vertex | Fragment;
}
