using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions;
using Puck.Vulkan;
using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;

namespace Puck.Demo;

/// <summary>
/// A bespoke, SURFACE-LESS Vulkan device context for the reverse cross-backend producer: a Vulkan instance + logical
/// device pinned to the Direct3D 12 host's adapter (LUID-matched) with NO swapchain and NO window surface, since the
/// Direct3D 12 host owns the window. It is the off-host counterpart of <c>VulkanRenderer</c> (which is swapchain-bound)
/// — built from the SAME instance/physical-device/logical-device factories but with a surface-less physical-device
/// pick by adapter LUID. Owns the instance and the logical device; dispose when finished.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
internal sealed class VulkanComputeDeviceContext : IVulkanDeviceContext, IGpuDeviceContext, IDisposable {
    private const string ApplicationName = "Puck.Demo (reverse compute producer)";

    private readonly VulkanInstance m_instance;
    private readonly VulkanLogicalDevice m_logicalDevice;
    private readonly VkPhysicalDevice m_physicalDevice;
    private bool m_disposed;

    private VulkanComputeDeviceContext(VulkanInstance instance, VkPhysicalDevice physicalDevice, VulkanLogicalDevice logicalDevice) {
        m_instance = instance;
        m_logicalDevice = logicalDevice;
        m_physicalDevice = physicalDevice;
    }

    /// <summary>Boots a surface-less Vulkan device on the physical device whose adapter LUID matches the Direct3D 12
    /// host's, using the bespoke compute provider's instance/physical-device/logical-device factories.</summary>
    /// <param name="adapterLuid">The Direct3D 12 host adapter LUID the producer must run on (so the host-owned shared image opens).</param>
    /// <param name="services">The bespoke Vulkan compute service provider (resolves the Vulkan factories + APIs).</param>
    /// <returns>The bootstrapped device context.</returns>
    /// <exception cref="InvalidOperationException">No Vulkan physical device matches the host adapter LUID, or it has no graphics/compute queue family.</exception>
    public static VulkanComputeDeviceContext Create(long adapterLuid, IServiceProvider services) {
        ArgumentNullException.ThrowIfNull(services);

        var instanceFactory = services.GetRequiredService<IVulkanInstanceFactory>();
        var physicalDeviceApi = services.GetRequiredService<IVulkanPhysicalDeviceApi>();
        var logicalDeviceFactory = services.GetRequiredService<IVulkanLogicalDeviceFactory>();

        // The Win32 surface instance extensions are harmless without a window; the validation layer is enabled to
        // match the live host's [vulkan-debug] drain.
        var instance = instanceFactory.Create(
            applicationName: ApplicationName,
            displayKind: NativeDisplayKind.Win32,
            enableValidation: true
        );

        try {
            var physicalDevice = SelectByAdapterLuid(adapterLuid: adapterLuid, instance: instance, physicalDeviceApi: physicalDeviceApi);
            var logicalDevice = logicalDeviceFactory.Create(instance: instance, physicalDevice: physicalDevice);

            return new VulkanComputeDeviceContext(instance: instance, physicalDevice: physicalDevice, logicalDevice: logicalDevice);
        } catch {
            instance.Dispose();

            throw;
        }
    }

    // The surface-less mirror of VulkanPhysicalDeviceSelector: pick the physical device whose reported LUID equals the
    // Direct3D 12 host adapter's (both APIs report the same DXGI adapter LUID for one GPU), then take its first
    // graphics-capable queue family — graphics always implies compute, and the producer never presents, so a single
    // family serves both slots of the selection.
    private static VkPhysicalDevice SelectByAdapterLuid(long adapterLuid, VulkanInstance instance, IVulkanPhysicalDeviceApi physicalDeviceApi) {
        var physicalDevices = physicalDeviceApi.EnumeratePhysicalDevices(instanceHandle: instance.Handle);

        if (physicalDevices.Count == 0) {
            throw new InvalidOperationException(message: "No Vulkan physical devices were reported for the bespoke producer instance.");
        }

        foreach (var physicalDeviceHandle in physicalDevices) {
            if (adapterLuid != physicalDeviceApi.GetDeviceLuid(instanceHandle: instance.Handle, physicalDeviceHandle: physicalDeviceHandle)) {
                continue;
            }

            foreach (var queueFamily in physicalDeviceApi.GetQueueFamilies(instanceHandle: instance.Handle, physicalDeviceHandle: physicalDeviceHandle)) {
                if (
                    (queueFamily.QueueCount == 0) ||
                    ((queueFamily.Flags & VkQueueFlags.Graphics) == 0)
                ) {
                    continue;
                }

                return new VkPhysicalDevice(
                    deviceType: physicalDeviceApi.GetPhysicalDeviceType(instanceHandle: instance.Handle, physicalDeviceHandle: physicalDeviceHandle),
                    handle: physicalDeviceHandle,
                    queueFamilySelection: new VulkanQueueFamilySelection(graphicsFamilyIndex: queueFamily.Index, presentFamilyIndex: queueFamily.Index)
                );
            }

            throw new InvalidOperationException(message: $"The Vulkan physical device matching the Direct3D 12 host adapter (LUID 0x{adapterLuid:X16}) exposes no graphics/compute queue family.");
        }

        throw new InvalidOperationException(message: $"No Vulkan physical device matches the Direct3D 12 host adapter LUID 0x{adapterLuid:X16}; the reverse cross-backend producer cannot import the host's shared image.");
    }

    /// <inheritdoc/>
    public VulkanInstance Instance => m_instance;
    /// <inheritdoc/>
    public bool IsInitialized => !m_disposed;
    /// <inheritdoc/>
    public VulkanLogicalDevice LogicalDevice => m_logicalDevice;
    /// <inheritdoc/>
    public VkPhysicalDevice PhysicalDevice => m_physicalDevice;
    /// <inheritdoc/>
    public VulkanSurface Surface => throw new InvalidOperationException(message: "The bespoke compute producer device is surface-less; the Direct3D 12 host owns the window surface.");

    /// <inheritdoc/>
    nint IGpuDeviceContext.DeviceHandle => m_logicalDevice.Handle;
    /// <inheritdoc/>
    void IGpuDeviceContext.WaitIdle() => m_logicalDevice.WaitIdle();

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        m_logicalDevice.Dispose();
        m_instance.Dispose();
    }
}
