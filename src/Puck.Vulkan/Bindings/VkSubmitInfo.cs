using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Describes a batch of command buffers submitted to a queue with <c>vkQueueSubmit</c>, together with the
/// semaphores it waits on before execution and signals on completion.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkSubmitInfo (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkSubmitInfo {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_SUBMIT_INFO</c>).</summary>
    public uint SType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>The number of entries in the <see cref="PWaitSemaphores"/> and <see cref="PWaitDstStageMask"/> arrays.</summary>
    public uint WaitSemaphoreCount;
    /// <summary>A pointer to an array of <c>VkSemaphore</c> handles the submission waits on before executing.</summary>
    public nint PWaitSemaphores;
    /// <summary>A pointer to an array of <c>VkPipelineStageFlags</c> values, one per wait semaphore, giving the stage at which each wait occurs.</summary>
    public nint PWaitDstStageMask;
    /// <summary>The number of entries in the <see cref="PCommandBuffers"/> array.</summary>
    public uint CommandBufferCount;
    /// <summary>A pointer to an array of <c>VkCommandBuffer</c> handles to execute in the batch.</summary>
    public nint PCommandBuffers;
    /// <summary>The number of entries in the <see cref="PSignalSemaphores"/> array.</summary>
    public uint SignalSemaphoreCount;
    /// <summary>A pointer to an array of <c>VkSemaphore</c> handles signaled when the batch completes.</summary>
    public nint PSignalSemaphores;
}
