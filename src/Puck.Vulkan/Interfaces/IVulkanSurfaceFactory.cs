using Puck.Vulkan.Interop;

namespace Puck.Vulkan.Interfaces;

/// <summary>
/// Creates a <see cref="VulkanSurface"/> from a native window binding, dispatching to the correct
/// platform-specific surface creation path.
/// </summary>
public interface IVulkanSurfaceFactory {
    /// <summary>Creates a presentation surface for a native window.</summary>
    /// <param name="instanceHandle">The native <c>VkInstance</c> handle the surface belongs to.</param>
    /// <param name="binding">The platform-specific native window binding to create the surface from.</param>
    /// <returns>A new, owning <see cref="VulkanSurface"/>.</returns>
    VulkanSurface Create(nint instanceHandle, NativeSurfaceBinding binding);
}
