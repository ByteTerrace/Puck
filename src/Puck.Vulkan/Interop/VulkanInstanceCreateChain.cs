using System.Runtime.InteropServices;
using Puck.Vulkan.Bindings;

namespace Puck.Vulkan.Interop;

/// <summary>
/// The structures <c>vkCreateInstance</c> reads through its create-info's <c>pNext</c> when validation is on: the
/// validation features, which enable synchronization validation, then the debug-utils messenger that reports what the
/// layer finds during instance creation and destruction. <see cref="VulkanNativeInstanceApi.LinkCreateChain"/> fills
/// and links a chain at a fixed address, so the pointers between its members stay valid for the create call.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct VulkanInstanceCreateChain {
    /// <summary><c>VK_STRUCTURE_TYPE_VALIDATION_FEATURES_EXT</c>.</summary>
    public const uint ValidationFeaturesStructureType = 1000247000;
    /// <summary><c>VK_VALIDATION_FEATURE_ENABLE_SYNCHRONIZATION_VALIDATION_EXT</c>.</summary>
    public const uint SynchronizationValidation = 4;
    /// <summary>The instance extension that declares <see cref="VkValidationFeaturesExt"/>, which the validation layer
    /// provides.</summary>
    public const string ValidationFeaturesExtension = "VK_EXT_validation_features";
    /// <summary>The Khronos validation layer.</summary>
    public const string ValidationLayer = "VK_LAYER_KHRONOS_validation";

    /// <summary>The validation features, first in the chain.</summary>
    public VkValidationFeaturesExt ValidationFeatures;
    /// <summary>The debug-utils messenger, after the validation features.</summary>
    public VkDebugUtilsMessengerCreateInfoExt Messenger;
    /// <summary>The one feature the validation features enable, <see cref="SynchronizationValidation"/>.</summary>
    public uint EnabledFeature;
}
