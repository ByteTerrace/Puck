using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Parameters describing a shader module to be created with <c>vkCreateShaderModule</c> from SPIR-V code.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkShaderModuleCreateInfo (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field
/// names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkShaderModuleCreateInfo {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_SHADER_MODULE_CREATE_INFO</c>).</summary>
    public uint SType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>Reserved for future use; must be zero.</summary>
    public uint Flags;
    /// <summary>The size, in bytes, of the SPIR-V code pointed to by <see cref="PCode"/>.</summary>
    public nuint CodeSize;
    /// <summary>A pointer to the SPIR-V code, as an array of 32-bit words.</summary>
    public nint PCode;
}
