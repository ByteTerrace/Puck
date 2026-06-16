namespace Puck.Vulkan.Messages;

/// <summary>
/// Describes an external (for example Direct3D 12) image to import as a sampleable Vulkan image, binding it to
/// memory imported from the shared NT handle.
/// </summary>
/// <param name="DeviceHandle">The native <c>VkDevice</c> handle.</param>
/// <param name="InstanceHandle">The native <c>VkInstance</c> handle, used to resolve memory support.</param>
/// <param name="PhysicalDeviceHandle">The native <c>VkPhysicalDevice</c> handle, used to resolve memory support.</param>
/// <param name="SharedHandle">The external NT handle to the shared resource.</param>
/// <param name="Width">The image width, in texels.</param>
/// <param name="Height">The image height, in texels.</param>
/// <param name="Format">The image format, as a <c>VkFormat</c> value; must match the external resource.</param>
public readonly record struct VulkanExternalImageImportRequest(
    nint DeviceHandle,
    nint InstanceHandle,
    nint PhysicalDeviceHandle,
    nint SharedHandle,
    uint Width,
    uint Height,
    uint Format
);
