using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Windowing;
using Puck.Memory;
using Puck.Vulkan;
using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Presentation;
using Xunit;

namespace Puck.Testing;

/// <summary>A Vulkan device with no surface, for device laws: the first physical device with a graphics queue family, a
/// discrete one first, brought up through the presenter's own registrations and holding the services
/// <see cref="VulkanPresenterServiceRegistration.DeviceServices"/> creates for it, as a presented device's are. Its
/// instance runs under the Khronos validation layer as <see cref="Validation"/> says, unless the law asks otherwise, and
/// the layer writes what it finds to standard error as <c>[vulkan-debug] validation</c> lines.</summary>
internal sealed class HeadlessVulkanDevice : IVulkanDeviceContext, IGpuDeviceContext, IDisposable {
    private readonly ServiceProvider m_provider;

    /// <summary>Whether a device law's instance runs under the validation layer when the law does not say: the one
    /// switch every Vulkan device law follows.</summary>
    public const bool Validation = true;

    private HeadlessVulkanDevice(ServiceProvider provider, VulkanInstance instance, VulkanLogicalDevice device, string name, long adapterLuid) {
        m_provider = provider;
        AdapterLuid = adapterLuid;
        Instance = instance;
        LogicalDevice = device;
        Name = name;
        Services = VulkanPresenterServiceRegistration.DeviceServices(serviceProvider: provider)(this);
    }

    public long AdapterLuid { get; }
    public GpuDeviceCapabilities? Capabilities => LogicalDevice.Capabilities;
    public GpuDeviceIdentity? Identity => LogicalDevice.Identity;
    public VulkanInstance Instance { get; }
    public VulkanLogicalDevice LogicalDevice { get; }
    public GpuMemoryProfile MemoryProfile => LogicalDevice.MemoryProfile;
    /// <summary>Gets the physical device's name, as its driver reports it.</summary>
    public string Name { get; }
    public VkPhysicalDevice PhysicalDevice => LogicalDevice.PhysicalDevice;
    public GpuDeviceServices Services { get; }
    public VulkanSurface Surface => throw new NotSupportedException(message: "A headless device has no surface.");
    /// <summary>Gets whether the instance was created with the validation layer enabled, as the instance reports its
    /// layers.</summary>
    public bool IsValidated => Instance.EnabledLayers.Contains(value: VulkanInstanceCreateChain.ValidationLayer);

    /// <summary>Brings the device up; skips the calling law by name when the host has no Vulkan loader, driver or device
    /// with a graphics queue family, or no validation layer when the device asks for one.</summary>
    /// <param name="applicationName">The application name the instance is created with.</param>
    /// <param name="validation">Whether the instance runs under the validation layer.</param>
    /// <returns>The device, owned by the caller.</returns>
    public static HeadlessVulkanDevice Create(string applicationName, bool validation = Validation) {
        var provider = new ServiceCollection()
            .AddPuckAllocator()
            .AddVulkanNativeApis()
            .AddVulkanFactories()
            .AddSingleton(implementationInstance: new VulkanRendererOptions { ApplicationName = applicationName, EnableValidation = validation })
            .AddSingleton(implementationInstance: new VulkanQueueSubmitter())
            .BuildServiceProvider();
        VulkanInstance? instance = null;

        try {
            try {
                instance = provider.GetRequiredService<IVulkanInstanceFactory>().Create(
                    applicationName: applicationName,
                    displayKind: NativeDisplayKind.Win32,
                    enableValidation: validation
                );
            } catch (GpuDeviceUnavailableException exception) {
                Assert.Skip(reason: (validation
                    ? $"no Vulkan loader, driver or validation layer: {exception.Message}"
                    : $"no Vulkan loader or driver: {exception.Message}"));
            }

            var physicalDeviceApi = provider.GetRequiredService<IVulkanPhysicalDeviceApi>();
            var candidates = physicalDeviceApi.EnumeratePhysicalDevices(instance: instance.Commands)
                .Select(selector: handle => (
                    Handle: handle,
                    Type: physicalDeviceApi.GetPhysicalDeviceType(instance: instance.Commands, physicalDeviceHandle: handle),
                    Graphics: physicalDeviceApi.GetQueueFamilies(instance: instance.Commands, physicalDeviceHandle: handle)
                        .FirstOrDefault(predicate: static family => ((0U != family.QueueCount) && (0 != (family.Flags & VkQueueFlags.Graphics))))
                ))
                .Where(predicate: static candidate => (0U != candidate.Graphics.QueueCount))
                .OrderBy(keySelector: static candidate => ((candidate.Type == VkPhysicalDeviceType.DiscreteGpu) ? 0 : 1))
                .ToArray();

            if (candidates.Length == 0) {
                Assert.Skip(reason: "no Vulkan device with a graphics queue family on this host");
            }

            var chosen = candidates[0];
            var physicalDevice = new VkPhysicalDevice(
                deviceType: chosen.Type,
                handle: chosen.Handle,
                queueFamilySelection: new VulkanQueueFamilySelection(
                    graphicsFamilyIndex: chosen.Graphics.Index,
                    presentFamilyIndex: chosen.Graphics.Index
                )
            );
            VulkanLogicalDevice device;

            try {
                device = provider.GetRequiredService<IVulkanLogicalDeviceFactory>().Create(
                    instance: instance,
                    physicalDevice: physicalDevice
                );
            } catch (GpuDeviceUnavailableException exception) {
                Assert.Skip(reason: $"no usable Vulkan device: {exception.Message}");

                throw;
            }

            return new HeadlessVulkanDevice(
                adapterLuid: physicalDeviceApi.GetDeviceLuid(instance: instance.Commands, physicalDeviceHandle: chosen.Handle),
                device: device,
                instance: instance,
                name: physicalDeviceApi.GetDeviceName(instance: instance.Commands, physicalDeviceHandle: chosen.Handle),
                provider: provider
            );
        } catch {
            instance?.Dispose();
            provider.Dispose();

            throw;
        }
    }
    public void Dispose() {
        LogicalDevice.Dispose();
        Instance.Dispose();
        m_provider.Dispose();
    }
    public void WaitIdle() => LogicalDevice.WaitIdle();
}
