using Puck.Vulkan.Bindings;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan.Interfaces;

/// <summary>
/// Wraps the native command pool and command buffer allocation entry points.
/// </summary>
public interface IVulkanCommandResourcesApi {
    /// <summary>Allocates command buffers from a command pool.</summary>
    /// <param name="request">The allocation parameters, including the source command pool.</param>
    /// <param name="buffer">A pointer to an array that receives the allocated native <c>VkCommandBuffer</c> handles.</param>
    /// <param name="commandBufferCount">The number of command buffers to allocate.</param>
    /// <returns>A <see cref="VkResult"/> indicating whether the buffers were allocated successfully.</returns>
    VkResult AllocateCommandBuffers(VulkanCommandBufferAllocateRequest request, nint buffer, uint commandBufferCount);
    /// <summary>Creates a command pool.</summary>
    /// <param name="request">The command pool creation parameters.</param>
    /// <param name="commandPoolHandle">When this method returns, the created native <c>VkCommandPool</c> handle.</param>
    /// <returns>A <see cref="VkResult"/> indicating whether the command pool was created successfully.</returns>
    VkResult CreateCommandPool(VulkanCommandPoolCreateRequest request, out nint commandPoolHandle);
    /// <summary>Destroys a command pool and the command buffers allocated from it.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle that owns the command pool.</param>
    /// <param name="commandPoolHandle">The native <c>VkCommandPool</c> handle to destroy.</param>
    void DestroyCommandPool(nint deviceHandle, nint commandPoolHandle);
}
