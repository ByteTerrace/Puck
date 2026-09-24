using Puck.Vulkan.Interfaces;

namespace Puck.Vulkan.Interop;

/// <summary>
/// Owns a native render pass (<c>VkRenderPass</c>) handle and destroys it when disposed.
/// </summary>
public sealed class VulkanRenderPass : IDisposable {
    private readonly IVulkanRenderPassApi m_renderPassApi;

    private bool m_disposed;

    /// <summary>Gets the command table of the logical device that owns the render pass.</summary>
    public VulkanDeviceCommands Device { get; }
    /// <summary>Gets the native <c>VkRenderPass</c> handle, or zero once the render pass has been disposed.</summary>
    public nint Handle { get; private set; }

    /// <summary>Initializes a new instance of the <see cref="VulkanRenderPass"/> class, taking ownership of an existing native render pass handle.</summary>
    /// <param name="device">The command table of the logical device that owns the render pass.</param>
    /// <param name="renderPassHandle">The native <c>VkRenderPass</c> handle to own.</param>
    /// <param name="renderPassApi">The API used to destroy the render pass on disposal.</param>
    /// <exception cref="ArgumentNullException"><paramref name="device"/> or <paramref name="renderPassApi"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="renderPassHandle"/> is zero.</exception>
    public VulkanRenderPass(
        VulkanDeviceCommands device,
        nint renderPassHandle,
        IVulkanRenderPassApi renderPassApi
    ) {
        ArgumentNullException.ThrowIfNull(argument: renderPassApi);

        ArgumentNullException.ThrowIfNull(argument: device);

        VulkanArgument.RequireHandle(
            handle: renderPassHandle,
            handleDescription: "render-pass",
            paramName: nameof(renderPassHandle)
        );

        Device = device;
        Handle = renderPassHandle;
        m_renderPassApi = renderPassApi;
    }

    /// <summary>Destroys the owned render pass handle. Safe to call more than once.</summary>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_renderPassApi.DestroyRenderPass(
            device: Device,
            renderPassHandle: Handle
        );
        Handle = 0;
        m_disposed = true;
    }
}
