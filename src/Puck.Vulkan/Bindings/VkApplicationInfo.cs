using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Identifies the application and engine to the implementation and requests a Vulkan API version. Supplied
/// to <c>vkCreateInstance</c> via <c>VkInstanceCreateInfo</c>.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkApplicationInfo (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkApplicationInfo {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_APPLICATION_INFO</c>).</summary>
    public uint StructureType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint Next;
    /// <summary>A pointer to a null-terminated UTF-8 string naming the application, or <see langword="null"/>.</summary>
    public nint ApplicationName;
    /// <summary>An unsigned, developer-supplied version number for the application.</summary>
    public uint ApplicationVersion;
    /// <summary>A pointer to a null-terminated UTF-8 string naming the engine used to create the application, or <see langword="null"/>.</summary>
    public nint EngineName;
    /// <summary>An unsigned, developer-supplied version number for the engine.</summary>
    public uint EngineVersion;
    /// <summary>The highest version of the Vulkan API the application is designed to use, packed as by <c>VK_MAKE_API_VERSION</c>. The patch component is ignored.</summary>
    public uint ApiVersion;
}
