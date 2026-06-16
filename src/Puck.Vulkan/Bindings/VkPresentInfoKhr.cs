using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Describes a present operation submitted to a queue with <c>vkQueuePresentKHR</c>: the swapchains and
/// image indices to present, and the semaphores waited on before presentation.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkPresentInfoKHR (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkPresentInfoKhr {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_PRESENT_INFO_KHR</c>).</summary>
    public uint SType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>The number of entries in the <see cref="PWaitSemaphores"/> array.</summary>
    public uint WaitSemaphoreCount;
    /// <summary>A pointer to an array of <c>VkSemaphore</c> handles waited on before the images are presented.</summary>
    public nint PWaitSemaphores;
    /// <summary>The number of swapchains being presented to; also the length of <see cref="PSwapchains"/>, <see cref="PImageIndices"/>, and (if present) <see cref="PResults"/>.</summary>
    public uint SwapchainCount;
    /// <summary>A pointer to an array of <c>VkSwapchainKHR</c> handles being presented to.</summary>
    public nint PSwapchains;
    /// <summary>A pointer to an array of indices selecting the image to present from each swapchain.</summary>
    public nint PImageIndices;
    /// <summary>A pointer to an array of <c>VkResult</c> values receiving the per-swapchain present results, or <see langword="null"/>.</summary>
    public nint PResults;
}
