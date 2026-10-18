using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Layer settings chained into <c>vkCreateInstance</c>'s create-info, which a layer reads in place of its settings file.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkLayerSettingsCreateInfoEXT (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field
/// names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkLayerSettingsCreateInfoExt {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value
    /// (<c>VK_STRUCTURE_TYPE_LAYER_SETTINGS_CREATE_INFO_EXT</c>).</summary>
    public uint StructureType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint Next;
    /// <summary>The number of settings.</summary>
    public uint SettingCount;
    /// <summary>A pointer to an array of <see cref="VkLayerSettingExt"/> values.</summary>
    public nint Settings;
}
/// <summary>
/// One layer setting: the layer and setting it names, and its values.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkLayerSettingEXT (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkLayerSettingExt {
    /// <summary>A pointer to the null-terminated UTF-8 name of the layer the setting is for.</summary>
    public nint LayerName;
    /// <summary>A pointer to the null-terminated UTF-8 name of the setting.</summary>
    public nint SettingName;
    /// <summary>The type of the values, as a <c>VkLayerSettingTypeEXT</c> value.</summary>
    public uint Type;
    /// <summary>The number of values.</summary>
    public uint ValueCount;
    /// <summary>A pointer to the values, an array of <see cref="Type"/>.</summary>
    public nint Values;
}
