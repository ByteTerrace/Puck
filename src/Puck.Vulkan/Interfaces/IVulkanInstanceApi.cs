using Puck.Vulkan.Bindings;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan.Interfaces;

/// <summary>
/// Wraps the native Vulkan instance entry points (<c>vkCreateInstance</c> and <c>vkDestroyInstance</c>).
/// </summary>
public interface IVulkanInstanceApi {
    /// <summary>Creates a Vulkan instance.</summary>
    /// <param name="request">The instance creation parameters.</param>
    /// <param name="instanceHandle">When this method returns, the created native <c>VkInstance</c> handle.</param>
    /// <returns>A <see cref="VkResult"/> indicating whether the instance was created successfully.</returns>
    VkResult CreateInstance(VulkanInstanceCreateRequest request, out nint instanceHandle);
    /// <summary>Reports whether the Vulkan loader advertises the named instance extension (queried across the core
    /// and implicit-layer extensions via <c>vkEnumerateInstanceExtensionProperties</c>). Lets the factory enable an
    /// optional instance extension only when it is present. Best-effort: returns <see langword="false"/> when the
    /// enumeration entry point is unavailable.</summary>
    /// <param name="extensionName">The extension name to probe (e.g. <c>VK_EXT_debug_utils</c>).</param>
    /// <returns><see langword="true"/> when the extension is supported; otherwise <see langword="false"/>.</returns>
    bool HasInstanceExtension(string extensionName);
    /// <summary>Destroys a Vulkan instance.</summary>
    /// <param name="instanceHandle">The native <c>VkInstance</c> handle to destroy.</param>
    void DestroyInstance(nint instanceHandle);
    /// <summary>Creates a <c>VK_EXT_debug_utils</c> messenger that surfaces validation messages to the console, so
    /// the Vulkan backend reports them like the Direct3D 12 info queue does. Best-effort: returns zero when the
    /// extension (or its entry point) is unavailable.</summary>
    /// <param name="instanceHandle">The native <c>VkInstance</c> the messenger reports for.</param>
    /// <returns>The native <c>VkDebugUtilsMessengerEXT</c> handle, or zero when one could not be created.</returns>
    nint CreateDebugMessenger(nint instanceHandle);
    /// <summary>Destroys a debug-utils messenger created by <see cref="CreateDebugMessenger"/>. A no-op when either
    /// handle is zero.</summary>
    /// <param name="instanceHandle">The native <c>VkInstance</c> the messenger belongs to.</param>
    /// <param name="messengerHandle">The native <c>VkDebugUtilsMessengerEXT</c> handle to destroy.</param>
    void DestroyDebugMessenger(nint instanceHandle, nint messengerHandle);
}
