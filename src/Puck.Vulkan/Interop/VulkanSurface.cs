using Puck.Vulkan.Interfaces;

namespace Puck.Vulkan.Interop;

/// <summary>
/// Owns a native presentation surface (<c>VkSurfaceKHR</c>) handle and destroys it when disposed.
/// </summary>
public sealed class VulkanSurface : IDisposable {
    private readonly IVulkanSurfaceApi m_surfaceApi;

    private bool m_disposed;

    /// <summary>Gets the native display kind the surface was created for.</summary>
    public NativeDisplayKind DisplayKind { get; }
    /// <summary>Gets the native <c>VkSurfaceKHR</c> handle, or zero once the surface has been disposed.</summary>
    public nint Handle { get; private set; }
    /// <summary>Gets the command table of the instance that owns the surface.</summary>
    public VulkanInstanceCommands Instance { get; }

    /// <summary>Initializes a new instance of the <see cref="VulkanSurface"/> class, taking ownership of an existing native surface handle.</summary>
    /// <param name="instance">The command table of the instance that owns the surface.</param>
    /// <param name="surfaceHandle">The native <c>VkSurfaceKHR</c> handle to own.</param>
    /// <param name="displayKind">The native display kind the surface was created for.</param>
    /// <param name="surfaceApi">The API used to destroy the surface on disposal.</param>
    /// <exception cref="ArgumentNullException"><paramref name="instance"/> or <paramref name="surfaceApi"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="surfaceHandle"/> is zero.</exception>
    public VulkanSurface(
        VulkanInstanceCommands instance,
        nint surfaceHandle,
        NativeDisplayKind displayKind,
        IVulkanSurfaceApi surfaceApi
    ) {
        ArgumentNullException.ThrowIfNull(argument: surfaceApi);

        ArgumentNullException.ThrowIfNull(argument: instance);

        VulkanArgument.RequireHandle(
            handle: surfaceHandle,
            handleDescription: "surface",
            paramName: nameof(surfaceHandle)
        );

        Instance = instance;
        Handle = surfaceHandle;
        DisplayKind = displayKind;
        m_surfaceApi = surfaceApi;
    }

    /// <summary>Destroys the owned surface handle. Safe to call more than once.</summary>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_surfaceApi.DestroySurface(
            instance: Instance,
            surfaceHandle: Handle
        );
        Handle = 0;
        m_disposed = true;
    }
}
