using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan.Factories;

/// <summary>
/// The default <see cref="IVulkanInstanceFactory"/>: it selects the surface extension for the display kind,
/// enables the validation layer, with synchronization validation, when requested, and creates an owning
/// <see cref="VulkanInstance"/>.
/// </summary>
public sealed class VulkanInstanceFactory : IVulkanInstanceFactory {
    // VK_EXT_debug_utils serves TWO independent purposes: the validation messenger (surfaced only when validation is
    // on) AND the vkCmdBeginDebugUtilsLabelEXT command-buffer labels the debug-group seam records. The extension is
    // free without a messenger, so it is enabled whenever the loader supports it — decoupled from validation — so
    // debug groups reach a GPU capture even in a default (validation-off) run. The messenger stays validation-only.
    private const string DebugUtilsExtension = "VK_EXT_debug_utils";
    // VK_EXT_swapchain_colorspace lets a surface report its HDR color spaces beside SRGB_NONLINEAR_KHR. Enabling it adds
    // pairs to what a surface reports and changes nothing else, so it is enabled whenever the loader supports it; an SDR
    // output still takes an SRGB_NONLINEAR_KHR pair.
    private const string SwapchainColorSpaceExtension = "VK_EXT_swapchain_colorspace";

    private static readonly string[] CommonExtensions = [
        "VK_KHR_surface",
    ];
    private static readonly string[] OptionalExtensions = [
        DebugUtilsExtension,
        SwapchainColorSpaceExtension,
    ];
    private static readonly string[] ValidationLayers = [
        VulkanInstanceCreateChain.ValidationLayer,
    ];

    private readonly IVulkanInstanceApi m_instanceApi;

    // With validation on, the layer's own VK_EXT_validation_features declares the VkValidationFeaturesEXT the create
    // chain carries (VulkanNativeInstanceApi.LinkCreateChain), which enables synchronization validation.
    private IReadOnlyList<string> BuildExtensionNames(NativeDisplayKind displayKind, bool enableValidation) {
        string[] surfaceExtensions = displayKind switch {
            NativeDisplayKind.Vi => [.. CommonExtensions, "VK_NN_vi_surface",],
            NativeDisplayKind.Wayland => [.. CommonExtensions, "VK_KHR_wayland_surface",],
            NativeDisplayKind.Win32 => [.. CommonExtensions, "VK_KHR_win32_surface",],
            NativeDisplayKind.Xcb => [.. CommonExtensions, "VK_KHR_xcb_surface",],
            _ => throw new PlatformNotSupportedException(message: $"Vulkan instance creation is not implemented for display kind '{displayKind}'.")
        };

        return [
            .. surfaceExtensions,
            .. OptionalExtensions.Where(predicate: extension => m_instanceApi.HasInstanceExtension(
                extensionName: extension,
                layerName: null
            )),
            .. ((enableValidation && m_instanceApi.HasInstanceExtension(
                extensionName: VulkanInstanceCreateChain.ValidationFeaturesExtension,
                layerName: VulkanInstanceCreateChain.ValidationLayer
            ))
                ? [VulkanInstanceCreateChain.ValidationFeaturesExtension,]
                : Array.Empty<string>()),
        ];
    }

    /// <inheritdoc/>
    public VulkanInstance Create(
        string applicationName,
        NativeDisplayKind displayKind,
        bool enableValidation,
        TextWriter? debugOutput = null
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: applicationName);

        // The writer both messengers report to, held for the instance's life; none with validation off.
        var output = ((enableValidation && (debugOutput is not null))
            ? new VulkanDebugOutput(writer: debugOutput)
            : null);
        VulkanInstanceCreateRequest request;
        VkResult result;
        VulkanInstanceCommands? instance;

        // The first two loader calls. An absent loader, or one missing its entry points, is a host with no Vulkan.
        try {
            request = new VulkanInstanceCreateRequest(
                ApplicationName: applicationName,
                DebugUserData: (output?.UserData ?? 0),
                DisplayKind: displayKind,
                EnableValidation: enableValidation,
                ExtensionNames: BuildExtensionNames(
                    displayKind: displayKind,
                    enableValidation: enableValidation
                ),
                LayerNames: (enableValidation
                ? ValidationLayers
                : [])
            );
            result = m_instanceApi.CreateInstance(
                instance: out instance,
                request: request
            );
        } catch (Exception exception) when ((exception is DllNotFoundException or EntryPointNotFoundException)) {
            output?.Dispose();

            throw VulkanResultExtensions.Unavailable(
                innerException: exception,
                reason: $"no Vulkan loader: {exception.Message}"
            );
        }

        // No installable client driver makes vkCreateInstance fail (VK_ERROR_INCOMPATIBLE_DRIVER).
        if ((result != VkResult.Success) || (instance is null)) {
            output?.Dispose();
        }

        result.ThrowIfUnavailable(operation: "vkCreateInstance");

        if (instance is null) {
            throw VulkanResultExtensions.Unavailable(reason: "vkCreateInstance returned success without a valid instance handle.");
        }

        nint debugMessengerHandle = 0;

        // From here the instance is live and no owner holds it yet: whatever fails destroys what this call created,
        // messenger first, before the failure propagates.
        try {
            // With validation on, register the debug-utils messenger so validation messages reach the console — parity
            // with the Direct3D 12 info-queue drain. Best-effort: a zero handle just means no messenger.
            debugMessengerHandle = (enableValidation
                ? m_instanceApi.CreateDebugMessenger(
                    instance: instance,
                    userData: (output?.UserData ?? 0)
                )
                : 0
            );

            // A requested layer states whether it is live, so a validation run that prints no [vulkan-debug] line can
            // tell a clean run from one the layer never watched. The prefix is not [vulkan-debug], which a run's checks
            // fail on.
            if (enableValidation) {
                (debugOutput ?? Console.Error).WriteLine(value: ((0 != debugMessengerHandle)
                    ? VulkanInstance.ValidationLiveLine
                    : VulkanInstance.ValidationNotLiveLine));
            }

            return new(
                debugMessengerHandle: debugMessengerHandle,
                debugOutput: output,
                displayKind: displayKind,
                enabledExtensions: request.ExtensionNames,
                enabledLayers: request.LayerNames,
                instanceApi: m_instanceApi,
                instance: instance
            );
        } catch {
            m_instanceApi.DestroyDebugMessenger(
                instance: instance,
                messengerHandle: debugMessengerHandle
            );
            m_instanceApi.DestroyInstance(instance: instance);
            output?.Dispose();

            throw;
        }
    }

    /// <summary>Initializes a new instance of the <see cref="VulkanInstanceFactory"/> class.</summary>
    /// <param name="instanceApi">The instance API used to create and own the underlying instance.</param>
    /// <exception cref="ArgumentNullException"><paramref name="instanceApi"/> is <see langword="null"/>.</exception>
    public VulkanInstanceFactory(IVulkanInstanceApi instanceApi) {
        ArgumentNullException.ThrowIfNull(argument: instanceApi);

        m_instanceApi = instanceApi;
    }
}
