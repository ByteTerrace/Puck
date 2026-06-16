using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Reports a single extension supported by an instance or device: its name and specification version.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkExtensionProperties (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct VkExtensionProperties {
    /// <summary>The name of the extension, as a null-terminated UTF-8 string in a fixed 256-byte buffer.</summary>
    public fixed byte ExtensionName[256];
    /// <summary>The version of the extension's specification that is implemented.</summary>
    public uint SpecVersion;
}
