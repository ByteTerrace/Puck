using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;

namespace Puck.Vulkan.Factories;

/// <summary>
/// The default <see cref="IVulkanSurfaceFactory"/>: it dispatches to the platform-specific surface creation
/// path for the binding's display kind and returns an owning <see cref="VulkanSurface"/>.
/// </summary>
public sealed class VulkanSurfaceFactory : IVulkanSurfaceFactory {
    private readonly IVulkanSurfaceApi m_surfaceApi;

    /// <summary>Initializes a new instance of the <see cref="VulkanSurfaceFactory"/> class.</summary>
    /// <param name="surfaceApi">The surface API used to create and own the underlying surface.</param>
    /// <exception cref="ArgumentNullException"><paramref name="surfaceApi"/> is <see langword="null"/>.</exception>
    public VulkanSurfaceFactory(IVulkanSurfaceApi surfaceApi) {
        ArgumentNullException.ThrowIfNull(argument: surfaceApi);

        m_surfaceApi = surfaceApi;
    }

    private static TBinding Require<TBinding>(TBinding? binding, string requirement) where TBinding : struct =>
        (binding ?? throw new InvalidOperationException(message: requirement));

    /// <inheritdoc/>
    public VulkanSurface Create(
        VulkanInstanceCommands instance,
        NativeSurfaceBinding binding
    ) {
        ArgumentNullException.ThrowIfNull(argument: instance);

        nint surfaceHandle;

        var (result, operation) = (binding.DisplayKind switch {
            NativeDisplayKind.Vi => (m_surfaceApi.CreateViSurface(
                binding: Require(
                    binding: binding.Vi,
                    requirement: "A Nintendo Switch (VI) native surface binding requires a native window handle."
                ),
                instance: instance,
                surfaceHandle: out surfaceHandle
            ), "vkCreateViSurfaceNN"),
            NativeDisplayKind.Wayland => (m_surfaceApi.CreateWaylandSurface(
                binding: Require(
                    binding: binding.Wayland,
                    requirement: "A Wayland native surface binding requires display and surface handles."
                ),
                instance: instance,
                surfaceHandle: out surfaceHandle
            ), "vkCreateWaylandSurfaceKHR"),
            NativeDisplayKind.Win32 => (m_surfaceApi.CreateWin32Surface(
                binding: Require(
                    binding: binding.Win32,
                    requirement: "A Win32 native surface binding requires instance and window handles."
                ),
                instance: instance,
                surfaceHandle: out surfaceHandle
            ), "vkCreateWin32SurfaceKHR"),
            NativeDisplayKind.Xcb => (m_surfaceApi.CreateXcbSurface(
                binding: Require(
                    binding: binding.Xcb,
                    requirement: "An XCB native surface binding requires a connection and window."
                ),
                instance: instance,
                surfaceHandle: out surfaceHandle
            ), "vkCreateXcbSurfaceKHR"),
            _ => throw new PlatformNotSupportedException(message: $"Vulkan surface creation is not implemented for display kind '{binding.DisplayKind}'.")
        });

        result.ThrowIfFailed(operation: operation);

        if (0 == surfaceHandle) {
            throw new InvalidOperationException(message: $"{operation} returned success without a valid surface handle.");
        }

        return new(
            displayKind: binding.DisplayKind,
            instance: instance,
            surfaceApi: m_surfaceApi,
            surfaceHandle: surfaceHandle
        );
    }
}
