using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan.Apis;

/// <summary>Queries the capabilities, formats, and present modes available to a Vulkan surface.</summary>
public sealed class VulkanSwapchainSupportApi : IVulkanSwapchainSupportApi {
    private readonly IVulkanPhysicalDeviceApi m_physicalDeviceApi;

    /// <summary>Initializes a new instance of the <see cref="VulkanSwapchainSupportApi"/> class.</summary>
    /// <param name="physicalDeviceApi">The physical-device API whose surface queries are composed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="physicalDeviceApi"/> is <see langword="null"/>.</exception>
    public VulkanSwapchainSupportApi(IVulkanPhysicalDeviceApi physicalDeviceApi) {
        ArgumentNullException.ThrowIfNull(physicalDeviceApi);

        m_physicalDeviceApi = physicalDeviceApi;
    }

    /// <inheritdoc/>
    public VulkanSwapchainSupportDetails Query(
        VulkanInstance instance,
        VkPhysicalDevice physicalDevice,
        VulkanSurface surface
    ) {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(surface);

        if (surface.InstanceHandle != instance.Handle) {
            throw new InvalidOperationException(message: "The Vulkan surface was not created from the supplied Vulkan instance.");
        }

        var capabilities = m_physicalDeviceApi.GetSurfaceCapabilities(
            instanceHandle: instance.Handle,
            physicalDeviceHandle: physicalDevice.Handle,
            surfaceHandle: surface.Handle
        );
        var formats = m_physicalDeviceApi.GetSurfaceFormats(
            instanceHandle: instance.Handle,
            physicalDeviceHandle: physicalDevice.Handle,
            surfaceHandle: surface.Handle
        );
        var presentModes = m_physicalDeviceApi.GetPresentModes(
            instanceHandle: instance.Handle,
            physicalDeviceHandle: physicalDevice.Handle,
            surfaceHandle: surface.Handle
        );

        return new VulkanSwapchainSupportDetails(
            Capabilities: capabilities,
            PresentModes: presentModes,
            SurfaceFormats: formats
        );
    }
}
