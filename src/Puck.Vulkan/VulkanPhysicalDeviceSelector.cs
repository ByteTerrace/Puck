using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;

namespace Puck.Vulkan;

/// <summary>
/// Selects the best physical device for a surface. Devices that lack both a graphics-capable and a
/// present-capable queue family are rejected; the rest are scored by device type (discrete 400, integrated
/// 300, virtual 200, CPU 100), with a <c>+25</c> bonus when a single family serves both roles. The
/// highest-scoring device wins.
/// </summary>
public sealed class VulkanPhysicalDeviceSelector : IVulkanPhysicalDeviceSelector {
    private static int Score(
        VkPhysicalDeviceType deviceType,
        VulkanQueueFamilySelection queueFamilySelection
    ) {
        var score = deviceType switch {
            VkPhysicalDeviceType.DiscreteGpu => 400,
            VkPhysicalDeviceType.IntegratedGpu => 300,
            VkPhysicalDeviceType.VirtualGpu => 200,
            VkPhysicalDeviceType.Cpu => 100,
            _ => 0
        };

        if (queueFamilySelection.UsesSingleQueueFamily) {
            score += 25;
        }

        return score;
    }

    private readonly IVulkanPhysicalDeviceApi m_physicalDeviceApi;

    /// <summary>Initializes a new instance of the <see cref="VulkanPhysicalDeviceSelector"/> class.</summary>
    /// <param name="physicalDeviceApi">The API used to enumerate and inspect physical devices.</param>
    /// <exception cref="ArgumentNullException"><paramref name="physicalDeviceApi"/> is <see langword="null"/>.</exception>
    public VulkanPhysicalDeviceSelector(IVulkanPhysicalDeviceApi physicalDeviceApi) {
        ArgumentNullException.ThrowIfNull(physicalDeviceApi);

        m_physicalDeviceApi = physicalDeviceApi;
    }

    private bool TryCreateCandidate(
        nint instanceHandle,
        nint surfaceHandle,
        nint physicalDeviceHandle,
        out Candidate candidate
    ) {
        var queueFamilies = m_physicalDeviceApi.GetQueueFamilies(
            instanceHandle: instanceHandle,
            physicalDeviceHandle: physicalDeviceHandle
        );
        uint? graphicsFamilyIndex = null;
        uint? presentFamilyIndex = null;

        foreach (var queueFamily in queueFamilies) {
            if (queueFamily.QueueCount == 0) {
                continue;
            }

            var supportsGraphics = ((queueFamily.Flags & VkQueueFlags.Graphics) != 0);
            var supportsPresent = m_physicalDeviceApi.GetSurfaceSupport(
                instanceHandle: instanceHandle,
                physicalDeviceHandle: physicalDeviceHandle,
                queueFamilyIndex: queueFamily.Index,
                surfaceHandle: surfaceHandle
            );

            if (
                supportsGraphics &&
                (graphicsFamilyIndex is null)
            ) {
                graphicsFamilyIndex = queueFamily.Index;
            }

            if (
                supportsPresent &&
                (presentFamilyIndex is null)
            ) {
                presentFamilyIndex = queueFamily.Index;
            }

            if (
                supportsGraphics &&
                supportsPresent
            ) {
                graphicsFamilyIndex = queueFamily.Index;
                presentFamilyIndex = queueFamily.Index;
                break;
            }
        }

        if (
            (graphicsFamilyIndex is null) ||
            (presentFamilyIndex is null)
        ) {
            candidate = default;
            return false;
        }

        var deviceType = m_physicalDeviceApi.GetPhysicalDeviceType(
            instanceHandle: instanceHandle,
            physicalDeviceHandle: physicalDeviceHandle
        );
        var queueFamilySelection = new VulkanQueueFamilySelection(
            graphicsFamilyIndex: graphicsFamilyIndex.Value,
            presentFamilyIndex: presentFamilyIndex.Value
        );
        var device = new VkPhysicalDevice(
            deviceType: deviceType,
            handle: physicalDeviceHandle,
            queueFamilySelection: queueFamilySelection
        );

        candidate = new Candidate(
            Device: device,
            Score: Score(
                deviceType: deviceType,
                queueFamilySelection: queueFamilySelection
            )
        );
        return true;
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException"><paramref name="instance"/> or <paramref name="surface"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The surface was not created from <paramref name="instance"/>, no devices were reported, or no device supports both graphics and present for the surface.</exception>
    public VkPhysicalDevice Select(VulkanInstance instance, VulkanSurface surface) {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(surface);

        if (surface.InstanceHandle != instance.Handle) {
            throw new InvalidOperationException(message: "The Vulkan surface was not created from the supplied Vulkan instance.");
        }

        var physicalDevices = m_physicalDeviceApi.EnumeratePhysicalDevices(instanceHandle: instance.Handle);

        if (physicalDevices.Count == 0) {
            throw new InvalidOperationException(message: "No Vulkan physical devices were reported for the current instance.");
        }

        Candidate? bestCandidate = null;

        foreach (var physicalDeviceHandle in physicalDevices) {
            if (!TryCreateCandidate(
                candidate: out var candidate,
                instanceHandle: instance.Handle,
                physicalDeviceHandle: physicalDeviceHandle,
                surfaceHandle: surface.Handle
            )) {
                continue;
            }

            if (
                (bestCandidate is null) ||
                (candidate.Score > bestCandidate.Value.Score)
            ) {
                bestCandidate = candidate;
            }
        }

        if (bestCandidate is null) {
            throw new InvalidOperationException(message: "No Vulkan physical device supports both graphics and present operations for the active surface.");
        }

        return bestCandidate.Value.Device;
    }

    private readonly record struct Candidate(VkPhysicalDevice Device, int Score);
}
