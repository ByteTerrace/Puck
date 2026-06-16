using Puck.Platform;
using Puck.Vulkan.Interfaces;

namespace Puck.Vulkan.Interop;

/// <summary>
/// Owns a native Vulkan instance (<c>VkInstance</c>) handle and destroys it when disposed.
/// </summary>
public sealed class VulkanInstance : IDisposable {
    private bool m_disposed;
    private readonly IVulkanInstanceApi m_instanceApi;

    /// <summary>Gets the native display kind the instance was created for.</summary>
    public NativeDisplayKind DisplayKind { get; }
    /// <summary>Gets the names of the instance extensions that were enabled.</summary>
    public IReadOnlyList<string> EnabledExtensions { get; }
    /// <summary>Gets the names of the instance layers that were enabled.</summary>
    public IReadOnlyList<string> EnabledLayers { get; }
    /// <summary>Gets the native <c>VkInstance</c> handle, or zero once the instance has been disposed.</summary>
    public nint Handle { get; private set; }

    /// <summary>Initializes a new instance of the <see cref="VulkanInstance"/> class, taking ownership of an existing native instance handle.</summary>
    /// <param name="instanceHandle">The native <c>VkInstance</c> handle to own.</param>
    /// <param name="displayKind">The native display kind the instance was created for.</param>
    /// <param name="enabledExtensions">The names of the enabled instance extensions.</param>
    /// <param name="enabledLayers">The names of the enabled instance layers.</param>
    /// <param name="instanceApi">The API used to destroy the instance on disposal.</param>
    /// <exception cref="ArgumentNullException"><paramref name="enabledExtensions"/>, <paramref name="enabledLayers"/>, or <paramref name="instanceApi"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="instanceHandle"/> is zero.</exception>
    public VulkanInstance(
        nint instanceHandle,
        NativeDisplayKind displayKind,
        IReadOnlyList<string> enabledExtensions,
        IReadOnlyList<string> enabledLayers,
        IVulkanInstanceApi instanceApi
    ) {
        ArgumentNullException.ThrowIfNull(argument: enabledExtensions);
        ArgumentNullException.ThrowIfNull(argument: enabledLayers);
        ArgumentNullException.ThrowIfNull(argument: instanceApi);

        if (0 == instanceHandle) {
            throw new ArgumentException(
                message: "Vulkan instance handle must be non-zero.",
                paramName: nameof(instanceHandle)
            );
        }

        Handle = instanceHandle;
        DisplayKind = displayKind;
        EnabledExtensions = enabledExtensions.ToArray();
        EnabledLayers = enabledLayers.ToArray();
        m_instanceApi = instanceApi;
    }

    /// <summary>Destroys the owned instance handle. Safe to call more than once.</summary>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        if (0 != Handle) {
            m_instanceApi.DestroyInstance(instanceHandle: Handle);
            Handle = 0;
        }

        m_disposed = true;
    }
}
