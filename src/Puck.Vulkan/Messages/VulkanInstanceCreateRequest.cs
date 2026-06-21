
namespace Puck.Vulkan.Messages;

/// <summary>
/// Describes a Vulkan instance to create: the application identity, the display kind that drives
/// surface-extension selection, and the layers and extensions to enable.
/// </summary>
/// <param name="ApplicationName">The name reported to the implementation for the application.</param>
/// <param name="DisplayKind">The native display kind, which determines the platform surface extension to enable.</param>
/// <param name="EnableValidation">Whether the Vulkan validation layers are enabled.</param>
/// <param name="ExtensionNames">The names of the instance extensions to enable.</param>
/// <param name="LayerNames">The names of the instance layers to enable.</param>
public readonly record struct VulkanInstanceCreateRequest(
    string ApplicationName,
    NativeDisplayKind DisplayKind,
    bool EnableValidation,
    IReadOnlyList<string> ExtensionNames,
    IReadOnlyList<string> LayerNames
);
