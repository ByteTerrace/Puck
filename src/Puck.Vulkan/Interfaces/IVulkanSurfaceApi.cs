using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interop;

namespace Puck.Vulkan.Interfaces;

/// <summary>
/// Wraps the platform-specific surface creation entry points and the common surface destruction call. A
/// surface bridges a native window to Vulkan for presentation.
/// </summary>
public interface IVulkanSurfaceApi {
    /// <summary>Creates a Vulkan surface for an <c>nn::vi</c> layer.</summary>
    /// <param name="instance">The command table of the instance.</param>
    /// <param name="binding">The native window binding describing the <c>nn::vi</c> layer.</param>
    /// <param name="surfaceHandle">When this method returns, the created native <c>VkSurfaceKHR</c> handle.</param>
    /// <returns>A <see cref="VkResult"/> indicating whether the surface was created successfully.</returns>
    VkResult CreateViSurface(
        VulkanInstanceCommands instance,
        ViNativeSurfaceBinding binding,
        out nint surfaceHandle
    );
    /// <summary>Creates a Vulkan surface for a Wayland window.</summary>
    /// <param name="instance">The command table of the instance.</param>
    /// <param name="binding">The native window binding describing the Wayland display and surface.</param>
    /// <param name="surfaceHandle">When this method returns, the created native <c>VkSurfaceKHR</c> handle.</param>
    /// <returns>A <see cref="VkResult"/> indicating whether the surface was created successfully.</returns>
    VkResult CreateWaylandSurface(
        VulkanInstanceCommands instance,
        WaylandNativeSurfaceBinding binding,
        out nint surfaceHandle
    );
    /// <summary>Creates a Vulkan surface for a Win32 window.</summary>
    /// <param name="instance">The command table of the instance.</param>
    /// <param name="binding">The native window binding describing the Win32 instance and window handles.</param>
    /// <param name="surfaceHandle">When this method returns, the created native <c>VkSurfaceKHR</c> handle.</param>
    /// <returns>A <see cref="VkResult"/> indicating whether the surface was created successfully.</returns>
    VkResult CreateWin32Surface(
        VulkanInstanceCommands instance,
        Win32NativeSurfaceBinding binding,
        out nint surfaceHandle
    );
    /// <summary>Creates a Vulkan surface for an X11 window via XCB.</summary>
    /// <param name="instance">The command table of the instance.</param>
    /// <param name="binding">The native window binding describing the XCB connection and window.</param>
    /// <param name="surfaceHandle">When this method returns, the created native <c>VkSurfaceKHR</c> handle.</param>
    /// <returns>A <see cref="VkResult"/> indicating whether the surface was created successfully.</returns>
    VkResult CreateXcbSurface(
        VulkanInstanceCommands instance,
        XcbNativeSurfaceBinding binding,
        out nint surfaceHandle
    );
    /// <summary>Destroys a Vulkan surface; a zero handle is skipped.</summary>
    /// <param name="instance">The command table of the instance that owns the surface.</param>
    /// <param name="surfaceHandle">The native <c>VkSurfaceKHR</c> handle to destroy.</param>
    void DestroySurface(VulkanInstanceCommands instance, nint surfaceHandle);
}
