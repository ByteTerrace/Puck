using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan.Factories;

/// <summary>
/// The default <see cref="IVulkanInstanceFactory"/>: it selects the surface extension for the display kind,
/// enables the validation layer when requested, and creates an owning <see cref="VulkanInstance"/>.
/// </summary>
public sealed class VulkanInstanceFactory : IVulkanInstanceFactory {
    // VK_EXT_debug_utils is provided by the validation layer, so it is enabled alongside it; it carries the messenger
    // that surfaces validation messages to the console.
    private const string DebugUtilsExtension = "VK_EXT_debug_utils";

    private static readonly string[] CommonExtensions = [
        "VK_KHR_surface",
    ];
    private static readonly string[] ValidationLayers = [
        "VK_LAYER_KHRONOS_validation",
    ];

    private static IReadOnlyList<string> BuildExtensionNames(NativeDisplayKind displayKind, bool enableValidation) {
        string[] surfaceExtensions = displayKind switch {
            NativeDisplayKind.Vi => [.. CommonExtensions, "VK_NN_vi_surface",],
            NativeDisplayKind.Wayland => [.. CommonExtensions, "VK_KHR_wayland_surface",],
            NativeDisplayKind.Win32 => [.. CommonExtensions, "VK_KHR_win32_surface",],
            NativeDisplayKind.Xcb => [.. CommonExtensions, "VK_KHR_xcb_surface",],
            _ => throw new PlatformNotSupportedException(message: $"Vulkan instance creation is not implemented for display kind '{displayKind}'.")
        };

        return enableValidation
            ? [.. surfaceExtensions, DebugUtilsExtension]
            : surfaceExtensions;
    }

    private readonly IVulkanInstanceApi m_instanceApi;

    /// <summary>Initializes a new instance of the <see cref="VulkanInstanceFactory"/> class.</summary>
    /// <param name="instanceApi">The instance API used to create and own the underlying instance.</param>
    /// <exception cref="ArgumentNullException"><paramref name="instanceApi"/> is <see langword="null"/>.</exception>
    public VulkanInstanceFactory(IVulkanInstanceApi instanceApi) {
        ArgumentNullException.ThrowIfNull(argument: instanceApi);

        m_instanceApi = instanceApi;
    }

    /// <inheritdoc/>
    public VulkanInstance Create(
        string applicationName,
        NativeDisplayKind displayKind,
        bool enableValidation
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: applicationName);

        var request = new VulkanInstanceCreateRequest(
            ApplicationName: applicationName,
            DisplayKind: displayKind,
            EnableValidation: enableValidation,
            ExtensionNames: BuildExtensionNames(displayKind: displayKind, enableValidation: enableValidation),
            LayerNames: (enableValidation
                ? ValidationLayers
                : [])
        );
        var result = m_instanceApi.CreateInstance(
            instanceHandle: out var instanceHandle,
            request: request
        );

        result.ThrowIfFailed(operation: "vkCreateInstance");

        if (0 == instanceHandle) {
            throw new InvalidOperationException(message: "vkCreateInstance returned success without a valid instance handle.");
        }

        // With validation on, register the debug-utils messenger so validation messages reach the console — parity
        // with the Direct3D 12 info-queue drain. Best-effort: a zero handle just means no messenger.
        var debugMessengerHandle = (enableValidation
            ? m_instanceApi.CreateDebugMessenger(instanceHandle: instanceHandle)
            : 0);

        return new(
            debugMessengerHandle: debugMessengerHandle,
            displayKind: displayKind,
            enabledExtensions: request.ExtensionNames,
            enabledLayers: request.LayerNames,
            instanceApi: m_instanceApi,
            instanceHandle: instanceHandle
        );
    }
}
