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
    // The device-side capability floor: Vulkan 1.3 (SPIR-V 1.6). A device reporting less would be selected and then
    // reject every 1.6 shader module at vkCreateShaderModule, so the floor is enforced here at selection with a loud,
    // named failure rather than a cryptic crash later. All four GPUs Puck supports clear it on current drivers.
    private const uint RequiredApiVersion = (1u << 22) | (3u << 12);

    private readonly IVulkanPhysicalDeviceApi m_physicalDeviceApi;

    private static string FormatApiVersion(uint version) {
        return $"{(version >> 22)}.{(version >> 12) & 0x3FFu}.{version & 0xFFFu}";
    }
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
    private bool TryCreateCandidate(
        VulkanInstanceCommands instance,
        nint surfaceHandle,
        nint physicalDeviceHandle,
        out Candidate candidate
    ) {
        var queueFamilies = m_physicalDeviceApi.GetQueueFamilies(
            instance: instance,
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
                instance: instance,
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
            instance: instance,
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
    /// <exception cref="InvalidOperationException">The surface was not created from <paramref name="instance"/>.</exception>
    /// <exception cref="GpuDeviceUnavailableException">Enumeration failed, no devices were reported, no device supports both graphics and present for the surface, or the best device is below the Vulkan 1.3 floor.</exception>
    public VkPhysicalDevice Select(VulkanInstance instance, VulkanSurface surface) {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(surface);

        if (surface.Instance != instance.Commands) {
            throw new InvalidOperationException(message: "The Vulkan surface was not created from the supplied Vulkan instance.");
        }

        IReadOnlyList<nint> physicalDevices;

        try {
            physicalDevices = m_physicalDeviceApi.EnumeratePhysicalDevices(instance: instance.Commands);
        } catch (VulkanException exception) {
            throw VulkanResultExtensions.Unavailable(
                innerException: exception,
                reason: exception.Message
            );
        }

        if (physicalDevices.Count == 0) {
            throw VulkanResultExtensions.Unavailable(reason: "no Vulkan physical devices were reported for the current instance.");
        }

        Candidate? bestCandidate = null;

        foreach (var physicalDeviceHandle in physicalDevices) {
            if (!TryCreateCandidate(
                candidate: out var candidate,
                instance: instance.Commands,
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
            throw VulkanResultExtensions.Unavailable(reason: "no Vulkan physical device supports both graphics and present operations for the active surface.");
        }

        // Enforce the SPIR-V 1.6 device floor on the winner (the loader instance version can outrank the device's own
        // reported ApiVersion). Fail loud and named — Puck's kernels are compiled at vulkan1.3 and will not load below it.
        var selectedApiVersion = m_physicalDeviceApi.GetDeviceApiVersion(
            instance: instance.Commands,
            physicalDeviceHandle: bestCandidate.Value.Device.Handle
        );

        if (selectedApiVersion < RequiredApiVersion) {
            var deviceName = m_physicalDeviceApi.GetDeviceName(
                instance: instance.Commands,
                physicalDeviceHandle: bestCandidate.Value.Device.Handle
            );

            throw VulkanResultExtensions.Unavailable(reason:
                (((((string)$"Vulkan device '{deviceName}' reports API version {FormatApiVersion(version: selectedApiVersion)}, below the required {FormatApiVersion(version: RequiredApiVersion)} (SPIR-V 1.6) floor. Puck's shader kernels are compiled for Vulkan 1.3 ") +
                "and cannot load on this device. Puck supports exactly four GPUs — RTX 2070 (Turing), RTX 4070 (Ada), ") +
                "Steam Machine (AMD RDNA3), and Steam Deck (AMD RDNA2 Van Gogh) — all of which expose Vulkan 1.3 on current ") +
                "drivers; update your GPU driver or run on supported hardware."));
        }

        return bestCandidate.Value.Device;
    }

    /// <summary>Initializes a new instance of the <see cref="VulkanPhysicalDeviceSelector"/> class.</summary>
    /// <param name="physicalDeviceApi">The API used to enumerate and inspect physical devices.</param>
    /// <exception cref="ArgumentNullException"><paramref name="physicalDeviceApi"/> is <see langword="null"/>.</exception>
    public VulkanPhysicalDeviceSelector(IVulkanPhysicalDeviceApi physicalDeviceApi) {
        ArgumentNullException.ThrowIfNull(physicalDeviceApi);

        m_physicalDeviceApi = physicalDeviceApi;
    }

    private readonly record struct Candidate(VkPhysicalDevice Device, int Score);
}
