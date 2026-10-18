using System.Runtime.InteropServices;
using Puck.Vulkan.Bindings;

namespace Puck.Vulkan.Interop;

/// <summary>
/// The structures <c>vkCreateInstance</c> reads through its create-info's <c>pNext</c> when validation is on: the
/// validation features, which enable synchronization validation at every level and, at the full level, GPU-assisted
/// validation and the best-practices checks; at the full level the layer settings, which add NVIDIA's best practices;
/// then the debug-utils messenger that reports what the layer finds during instance creation and destruction.
/// <see cref="VulkanNativeInstanceApi.LinkCreateChain"/> fills and links a chain at a fixed address, so the pointers
/// between its members stay valid for the create call.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct VulkanInstanceCreateChain {
    /// <summary><c>VK_STRUCTURE_TYPE_VALIDATION_FEATURES_EXT</c>.</summary>
    public const uint ValidationFeaturesStructureType = 1000247000;
    /// <summary><c>VK_STRUCTURE_TYPE_LAYER_SETTINGS_CREATE_INFO_EXT</c>.</summary>
    public const uint LayerSettingsStructureType = 1000496000;
    /// <summary><c>VK_VALIDATION_FEATURE_ENABLE_GPU_ASSISTED_EXT</c>.</summary>
    public const uint GpuAssistedValidation = 0;
    /// <summary><c>VK_VALIDATION_FEATURE_ENABLE_BEST_PRACTICES_EXT</c>.</summary>
    public const uint BestPractices = 2;
    /// <summary><c>VK_VALIDATION_FEATURE_ENABLE_SYNCHRONIZATION_VALIDATION_EXT</c>.</summary>
    public const uint SynchronizationValidation = 4;
    /// <summary><c>VK_LAYER_SETTING_TYPE_BOOL32_EXT</c>.</summary>
    public const uint LayerSettingBool32 = 0;
    /// <summary>The instance extension that declares <see cref="VkValidationFeaturesExt"/>, which the validation layer
    /// provides.</summary>
    public const string ValidationFeaturesExtension = "VK_EXT_validation_features";
    /// <summary>The instance extension that declares <see cref="VkLayerSettingsCreateInfoExt"/>, which the validation
    /// layer provides.</summary>
    public const string LayerSettingsExtension = "VK_EXT_layer_settings";
    /// <summary>The Khronos validation layer.</summary>
    public const string ValidationLayer = "VK_LAYER_KHRONOS_validation";
    /// <summary>The validation layer's setting that turns on NVIDIA's best-practices checks.</summary>
    public const string NvidiaBestPracticesSetting = "validate_best_practices_nvidia";

    /// <summary>The validation features, first in the chain.</summary>
    public VkValidationFeaturesExt ValidationFeatures;
    /// <summary>The layer settings, after the validation features at the full level; unlinked at the basic level.</summary>
    public VkLayerSettingsCreateInfoExt LayerSettings;
    /// <summary>The one setting <see cref="LayerSettings"/> carries, <see cref="NvidiaBestPracticesSetting"/>.</summary>
    public VkLayerSettingExt NvidiaBestPractices;
    /// <summary>The debug-utils messenger, last in the chain.</summary>
    public VkDebugUtilsMessengerCreateInfoExt Messenger;
    /// <summary>The first feature the validation features enable, <see cref="SynchronizationValidation"/>.</summary>
    public uint EnabledFeature;
    /// <summary>The second, at the full level only: <see cref="GpuAssistedValidation"/>.</summary>
    public uint SecondEnabledFeature;
    /// <summary>The third, at the full level only: <see cref="BestPractices"/>.</summary>
    public uint ThirdEnabledFeature;
    /// <summary>The value of <see cref="NvidiaBestPractices"/>: <c>VK_TRUE</c>.</summary>
    public uint True;
}
