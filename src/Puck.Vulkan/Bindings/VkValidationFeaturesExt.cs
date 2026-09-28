using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Validation features to enable or disable in the validation layer, chained into <c>vkCreateInstance</c>'s create-info.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkValidationFeaturesEXT (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkValidationFeaturesExt {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_VALIDATION_FEATURES_EXT</c>).</summary>
    public uint StructureType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint Next;
    /// <summary>The number of features to enable.</summary>
    public uint EnabledValidationFeatureCount;
    /// <summary>A pointer to an array of <c>VkValidationFeatureEnableEXT</c> values naming the features to enable.</summary>
    public nint EnabledValidationFeatures;
    /// <summary>The number of features to disable.</summary>
    public uint DisabledValidationFeatureCount;
    /// <summary>A pointer to an array of <c>VkValidationFeatureDisableEXT</c> values naming the features to disable.</summary>
    public nint DisabledValidationFeatures;
}
