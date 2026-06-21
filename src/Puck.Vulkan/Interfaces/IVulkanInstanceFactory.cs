using Puck.Vulkan.Interop;

namespace Puck.Vulkan.Interfaces;

/// <summary>
/// Creates a fully configured <see cref="VulkanInstance"/>, selecting the surface extension for the display
/// kind and optionally enabling the validation layers.
/// </summary>
public interface IVulkanInstanceFactory {
    /// <summary>Creates a Vulkan instance for the given application and display kind.</summary>
    /// <param name="applicationName">The application name reported to the implementation.</param>
    /// <param name="displayKind">The native display kind, which selects the surface extension to enable.</param>
    /// <param name="enableValidation">Whether to enable the Vulkan validation layers.</param>
    /// <returns>A new, owning <see cref="VulkanInstance"/>.</returns>
    VulkanInstance Create(string applicationName, NativeDisplayKind displayKind, bool enableValidation);
}
