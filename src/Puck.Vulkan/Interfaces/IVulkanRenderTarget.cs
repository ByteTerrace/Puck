using Puck.Vulkan.Interop;

namespace Puck.Vulkan.Interfaces;

/// <summary>
/// A Vulkan render target that exposes its typed render pass, so a graphics pipeline can be created compatible with
/// the target regardless of whether it is a plain offscreen target or an exportable one.
/// </summary>
public interface IVulkanRenderTarget {
    /// <summary>Gets the render pass the target's framebuffer was created against.</summary>
    VulkanRenderPass RenderPass { get; }
}
