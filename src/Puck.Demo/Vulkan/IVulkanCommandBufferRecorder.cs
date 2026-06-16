using Puck.Assets;
using Puck.Vulkan.Interop;

namespace Puck.Vulkan;

public interface IVulkanCommandBufferRecorder {
    /// <summary>Records ONE swapchain image's command buffer. The caller must guarantee no submission
    /// of that buffer is pending (the presenter's in-flight fence wait); vkBeginCommandBuffer then
    /// implicitly resets it (the pool is created with VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT).</summary>
    void RecordImage(
        VulkanCommandResources commandResources,
        int imageIndex,
        VulkanFramebufferSet framebufferSet,
        VulkanRenderPass renderPass,
        IReadOnlyDictionary<AssetContentHash, VulkanGraphicsPipeline> graphicsPipelines,
        VulkanSwapchain swapchain,
        IReadOnlyList<VulkanDrawCommand> drawCommands
    );
}
