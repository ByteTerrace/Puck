using Puck.Platform;
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

    private VulkanSurface CreateViSurface(
        nint instanceHandle,
        NativeSurfaceBinding binding
    ) {
        if (binding.Vi is null) {
            throw new InvalidOperationException(message: "A Nintendo Switch (VI) native surface binding requires a native window handle.");
        }

        var result = m_surfaceApi.CreateViSurface(
            binding: binding.Vi.Value,
            instanceHandle: instanceHandle,
            surfaceHandle: out var surfaceHandle
        );

        result.ThrowIfFailed(operation: "vkCreateViSurfaceNN");

        if (0 == surfaceHandle) {
            throw new InvalidOperationException(message: "vkCreateViSurfaceNN returned success without a valid surface handle.");
        }

        return new(
            displayKind: binding.DisplayKind,
            instanceHandle: instanceHandle,
            surfaceApi: m_surfaceApi,
            surfaceHandle: surfaceHandle
        );
    }
    private VulkanSurface CreateWaylandSurface(
        nint instanceHandle,
        NativeSurfaceBinding binding
    ) {
        if (binding.Wayland is null) {
            throw new InvalidOperationException(message: "A Wayland native surface binding requires display and surface handles.");
        }

        var result = m_surfaceApi.CreateWaylandSurface(
            binding: binding.Wayland.Value,
            instanceHandle: instanceHandle,
            surfaceHandle: out var surfaceHandle
        );

        result.ThrowIfFailed(operation: "vkCreateWaylandSurfaceKHR");

        if (0 == surfaceHandle) {
            throw new InvalidOperationException(message: "vkCreateWaylandSurfaceKHR returned success without a valid surface handle.");
        }

        return new(
            displayKind: binding.DisplayKind,
            instanceHandle: instanceHandle,
            surfaceApi: m_surfaceApi,
            surfaceHandle: surfaceHandle
        );
    }
    private VulkanSurface CreateWin32Surface(
        nint instanceHandle,
        NativeSurfaceBinding binding
    ) {
        if (binding.Win32 is null) {
            throw new InvalidOperationException(message: "A Win32 native surface binding requires instance and window handles.");
        }

        var result = m_surfaceApi.CreateWin32Surface(
            binding: binding.Win32.Value,
            instanceHandle: instanceHandle,
            surfaceHandle: out var surfaceHandle
        );

        result.ThrowIfFailed(operation: "vkCreateWin32SurfaceKHR");

        if (0 == surfaceHandle) {
            throw new InvalidOperationException(message: "vkCreateWin32SurfaceKHR returned success without a valid surface handle.");
        }

        return new(
            displayKind: binding.DisplayKind,
            instanceHandle: instanceHandle,
            surfaceApi: m_surfaceApi,
            surfaceHandle: surfaceHandle
        );
    }
    private VulkanSurface CreateXcbSurface(
        nint instanceHandle,
        NativeSurfaceBinding binding
    ) {
        if (binding.Xcb is null) {
            throw new InvalidOperationException(message: "An XCB native surface binding requires a connection and window.");
        }

        var result = m_surfaceApi.CreateXcbSurface(
            binding: binding.Xcb.Value,
            instanceHandle: instanceHandle,
            surfaceHandle: out var surfaceHandle
        );

        result.ThrowIfFailed(operation: "vkCreateXcbSurfaceKHR");

        if (0 == surfaceHandle) {
            throw new InvalidOperationException(message: "vkCreateXcbSurfaceKHR returned success without a valid surface handle.");
        }

        return new(
            displayKind: binding.DisplayKind,
            instanceHandle: instanceHandle,
            surfaceApi: m_surfaceApi,
            surfaceHandle: surfaceHandle
        );
    }

    /// <inheritdoc/>
    public VulkanSurface Create(
        nint instanceHandle,
        NativeSurfaceBinding binding
    ) {
        if (0 == instanceHandle) {
            throw new ArgumentException(
                message: "Vulkan instance handle must be non-zero.",
                paramName: nameof(instanceHandle)
            );
        }

        return binding.DisplayKind switch {
            NativeDisplayKind.Vi => CreateViSurface(
                binding: binding,
                instanceHandle: instanceHandle
            ),
            NativeDisplayKind.Wayland => CreateWaylandSurface(
                binding: binding,
                instanceHandle: instanceHandle
            ),
            NativeDisplayKind.Win32 => CreateWin32Surface(
                binding: binding,
                instanceHandle: instanceHandle
            ),
            NativeDisplayKind.Xcb => CreateXcbSurface(
                binding: binding,
                instanceHandle: instanceHandle
            ),
            _ => throw new PlatformNotSupportedException(message: $"Vulkan surface creation is not implemented for display kind '{binding.DisplayKind}'.")
        };
    }
}
