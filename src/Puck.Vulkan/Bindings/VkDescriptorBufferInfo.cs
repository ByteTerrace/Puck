using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Specifies the buffer region bound to a buffer descriptor (uniform or storage buffer, including the
/// dynamic variants).
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkDescriptorBufferInfo (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkDescriptorBufferInfo {
    /// <summary>The buffer resource bound to the descriptor (a <c>VkBuffer</c> handle).</summary>
    public nint Buffer;
    /// <summary>The offset, in bytes, from the start of <see cref="Buffer"/> at which the bound region begins.</summary>
    public ulong Offset;
    /// <summary>The size, in bytes, of the bound region, or <c>VK_WHOLE_SIZE</c> to bind the remainder of the buffer.</summary>
    public ulong Range;
}
