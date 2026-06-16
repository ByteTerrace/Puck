using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan.Factories;

/// <summary>
/// The default <see cref="IVulkanRenderPassFactory"/>: it builds a present-oriented, single-color-attachment
/// render pass matching the swapchain's format and returns an owning <see cref="VulkanRenderPass"/>.
/// </summary>
public sealed class VulkanRenderPassFactory : IVulkanRenderPassFactory {
    private readonly IVulkanRenderPassApi m_renderPassApi;

    /// <summary>Initializes a new instance of the <see cref="VulkanRenderPassFactory"/> class.</summary>
    /// <param name="renderPassApi">The render-pass API used to create and own the underlying render pass.</param>
    /// <exception cref="ArgumentNullException"><paramref name="renderPassApi"/> is <see langword="null"/>.</exception>
    public VulkanRenderPassFactory(IVulkanRenderPassApi renderPassApi) {
        ArgumentNullException.ThrowIfNull(argument: renderPassApi);

        m_renderPassApi = renderPassApi;
    }

    /// <inheritdoc/>
    public VulkanRenderPass Create(
        VulkanLogicalDevice logicalDevice,
        VulkanSwapchain swapchain
    ) {
        ArgumentNullException.ThrowIfNull(argument: logicalDevice);
        ArgumentNullException.ThrowIfNull(argument: swapchain);

        var request = VulkanRenderPassRequests.Present(
            colorFormat: swapchain.ImageFormat,
            deviceHandle: logicalDevice.Handle
        );
        var result = m_renderPassApi.CreateRenderPass(
            renderPassHandle: out var renderPassHandle,
            request: request
        );

        result.ThrowIfFailed(operation: "vkCreateRenderPass");

        if (0 == renderPassHandle) {
            throw new InvalidOperationException(message: "vkCreateRenderPass returned success without a valid render-pass handle.");
        }

        return new(
            deviceHandle: logicalDevice.Handle,
            renderPassApi: m_renderPassApi,
            renderPassHandle: renderPassHandle
        );
    }
}
