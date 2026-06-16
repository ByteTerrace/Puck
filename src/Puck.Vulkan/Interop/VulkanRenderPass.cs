using Puck.Vulkan.Interfaces;

namespace Puck.Vulkan.Interop;

/// <summary>
/// Owns a native render pass (<c>VkRenderPass</c>) handle and destroys it when disposed.
/// </summary>
public sealed class VulkanRenderPass : IDisposable {
    private bool m_disposed;
    private readonly IVulkanRenderPassApi m_renderPassApi;

    /// <summary>Gets the native <c>VkDevice</c> handle that owns the render pass.</summary>
    public nint DeviceHandle { get; }
    /// <summary>Gets the native <c>VkRenderPass</c> handle, or zero once the render pass has been disposed.</summary>
    public nint Handle { get; private set; }

    /// <summary>Initializes a new instance of the <see cref="VulkanRenderPass"/> class, taking ownership of an existing native render pass handle.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle that owns the render pass.</param>
    /// <param name="renderPassHandle">The native <c>VkRenderPass</c> handle to own.</param>
    /// <param name="renderPassApi">The API used to destroy the render pass on disposal.</param>
    /// <exception cref="ArgumentNullException"><paramref name="renderPassApi"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="deviceHandle"/> or <paramref name="renderPassHandle"/> is zero.</exception>
    public VulkanRenderPass(
        nint deviceHandle,
        nint renderPassHandle,
        IVulkanRenderPassApi renderPassApi
    ) {
        ArgumentNullException.ThrowIfNull(argument: renderPassApi);

        if (0 == deviceHandle) {
            throw new ArgumentException(
                message: "Vulkan logical-device handle must be non-zero.",
                paramName: nameof(deviceHandle)
            );
        }

        if (0 == renderPassHandle) {
            throw new ArgumentException(
                message: "Vulkan render-pass handle must be non-zero.",
                paramName: nameof(renderPassHandle)
            );
        }

        DeviceHandle = deviceHandle;
        Handle = renderPassHandle;
        m_renderPassApi = renderPassApi;
    }

    /// <summary>Destroys the owned render pass handle. Safe to call more than once.</summary>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        if (0 != Handle) {
            m_renderPassApi.DestroyRenderPass(
                deviceHandle: DeviceHandle,
                renderPassHandle: Handle
            );
            Handle = 0;
        }

        m_disposed = true;
    }
}
