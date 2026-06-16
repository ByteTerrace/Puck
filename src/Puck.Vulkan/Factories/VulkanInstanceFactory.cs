using Puck.Platform;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan.Factories;

/// <summary>
/// The default <see cref="IVulkanInstanceFactory"/>: it selects the surface extension for the display kind,
/// enables the validation layer when requested, and creates an owning <see cref="VulkanInstance"/>.
/// </summary>
public sealed class VulkanInstanceFactory : IVulkanInstanceFactory {
    private static readonly string[] CommonExtensions = [
        "VK_KHR_surface",
    ];
    private static readonly string[] ValidationLayers = [
        "VK_LAYER_KHRONOS_validation",
    ];

    private static IReadOnlyList<string> BuildExtensionNames(NativeDisplayKind displayKind) {
        return displayKind switch {
            NativeDisplayKind.Vi => [.. CommonExtensions, "VK_NN_vi_surface",],
            NativeDisplayKind.Wayland => [.. CommonExtensions, "VK_KHR_wayland_surface",],
            NativeDisplayKind.Win32 => [.. CommonExtensions, "VK_KHR_win32_surface",],
            NativeDisplayKind.Xcb => [.. CommonExtensions, "VK_KHR_xcb_surface",],
            _ => throw new PlatformNotSupportedException(message: $"Vulkan instance creation is not implemented for display kind '{displayKind}'.")
        };
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
            ExtensionNames: BuildExtensionNames(displayKind: displayKind),
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

        return new(
            displayKind: displayKind,
            enabledExtensions: request.ExtensionNames,
            enabledLayers: request.LayerNames,
            instanceApi: m_instanceApi,
            instanceHandle: instanceHandle
        );
    }
}
