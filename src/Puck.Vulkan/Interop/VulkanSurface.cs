using Puck.Platform;
using Puck.Vulkan.Interfaces;

namespace Puck.Vulkan.Interop;

/// <summary>
/// Owns a native presentation surface (<c>VkSurfaceKHR</c>) handle and destroys it when disposed.
/// </summary>
public sealed class VulkanSurface : IDisposable {
    private bool m_disposed;
    private readonly IVulkanSurfaceApi m_surfaceApi;

    /// <summary>Gets the native display kind the surface was created for.</summary>
    public NativeDisplayKind DisplayKind { get; }
    /// <summary>Gets the native <c>VkSurfaceKHR</c> handle, or zero once the surface has been disposed.</summary>
    public nint Handle { get; private set; }
    /// <summary>Gets the native <c>VkInstance</c> handle that owns the surface.</summary>
    public nint InstanceHandle { get; }

    /// <summary>Initializes a new instance of the <see cref="VulkanSurface"/> class, taking ownership of an existing native surface handle.</summary>
    /// <param name="instanceHandle">The native <c>VkInstance</c> handle that owns the surface.</param>
    /// <param name="surfaceHandle">The native <c>VkSurfaceKHR</c> handle to own.</param>
    /// <param name="displayKind">The native display kind the surface was created for.</param>
    /// <param name="surfaceApi">The API used to destroy the surface on disposal.</param>
    /// <exception cref="ArgumentNullException"><paramref name="surfaceApi"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="instanceHandle"/> or <paramref name="surfaceHandle"/> is zero.</exception>
    public VulkanSurface(
        nint instanceHandle,
        nint surfaceHandle,
        NativeDisplayKind displayKind,
        IVulkanSurfaceApi surfaceApi
    ) {
        ArgumentNullException.ThrowIfNull(argument: surfaceApi);

        if (0 == instanceHandle) {
            throw new ArgumentException(
                message: "Vulkan instance handle must be non-zero.",
                paramName: nameof(instanceHandle)
            );
        }

        if (0 == surfaceHandle) {
            throw new ArgumentException(
                message: "Vulkan surface handle must be non-zero.",
                paramName: nameof(surfaceHandle)
            );
        }

        InstanceHandle = instanceHandle;
        Handle = surfaceHandle;
        DisplayKind = displayKind;
        m_surfaceApi = surfaceApi;
    }

    /// <summary>Destroys the owned surface handle. Safe to call more than once.</summary>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        if (0 != Handle) {
            m_surfaceApi.DestroySurface(
                instanceHandle: InstanceHandle,
                surfaceHandle: Handle
            );
            Handle = 0;
        }

        m_disposed = true;
    }
}
