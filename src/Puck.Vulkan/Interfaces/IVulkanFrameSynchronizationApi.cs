using Puck.Vulkan.Bindings;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan.Interfaces;

/// <summary>
/// Wraps the native entry points for the synchronization primitives used to coordinate a frame: fences
/// (CPU–GPU) and semaphores (GPU–GPU).
/// </summary>
public interface IVulkanFrameSynchronizationApi {
    /// <summary>Creates a fence.</summary>
    /// <param name="request">The fence creation parameters.</param>
    /// <param name="fenceHandle">When this method returns, the created native <c>VkFence</c> handle.</param>
    /// <returns>A <see cref="VkResult"/> indicating whether the fence was created successfully.</returns>
    VkResult CreateFence(VulkanFrameSynchronizationCreateRequest request, out nint fenceHandle);
    /// <summary>Creates a semaphore.</summary>
    /// <param name="request">The semaphore creation parameters.</param>
    /// <param name="semaphoreHandle">When this method returns, the created native <c>VkSemaphore</c> handle.</param>
    /// <returns>A <see cref="VkResult"/> indicating whether the semaphore was created successfully.</returns>
    VkResult CreateSemaphore(VulkanFrameSynchronizationCreateRequest request, out nint semaphoreHandle);
    /// <summary>Destroys a fence.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle that owns the fence.</param>
    /// <param name="fenceHandle">The native <c>VkFence</c> handle to destroy.</param>
    void DestroyFence(nint deviceHandle, nint fenceHandle);
    /// <summary>Destroys a semaphore.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle that owns the semaphore.</param>
    /// <param name="semaphoreHandle">The native <c>VkSemaphore</c> handle to destroy.</param>
    void DestroySemaphore(nint deviceHandle, nint semaphoreHandle);
    /// <summary>Resets a fence to the unsignaled state.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle that owns the fence.</param>
    /// <param name="fenceHandle">The native <c>VkFence</c> handle to reset.</param>
    /// <returns>A <see cref="VkResult"/> indicating whether the fence was reset successfully.</returns>
    VkResult ResetFence(nint deviceHandle, nint fenceHandle);
    /// <summary>Waits for a fence to become signaled, or until the timeout elapses.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle that owns the fence.</param>
    /// <param name="fenceHandle">The native <c>VkFence</c> handle to wait on.</param>
    /// <param name="timeout">The maximum time to wait, in nanoseconds.</param>
    /// <returns><see cref="VkResult.Success"/> if the fence became signaled, <see cref="VkResult.Timeout"/> if the timeout elapsed first, or an error code.</returns>
    VkResult WaitForFence(nint deviceHandle, nint fenceHandle, ulong timeout);
}
