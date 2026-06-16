using Puck.Vulkan.Messages;

namespace Puck.Vulkan.Interfaces;

/// <summary>
/// Wraps the native entry points for descriptor pools, descriptor set allocation, samplers, and descriptor
/// set updates.
/// </summary>
public interface IVulkanDescriptorApi {
    /// <summary>Allocates a single descriptor set from a pool.</summary>
    /// <param name="request">The allocation parameters, including the pool and set layout.</param>
    /// <returns>The allocated native <c>VkDescriptorSet</c> handle.</returns>
    nint AllocateSet(VulkanDescriptorSetAllocateRequest request);
    /// <summary>Creates a descriptor pool.</summary>
    /// <param name="request">The pool creation parameters.</param>
    /// <returns>The created native <c>VkDescriptorPool</c> handle.</returns>
    nint CreatePool(VulkanDescriptorPoolCreateRequest request);
    /// <summary>Creates a sampler.</summary>
    /// <param name="request">The sampler creation parameters.</param>
    /// <returns>The created native <c>VkSampler</c> handle.</returns>
    nint CreateSampler(VulkanSamplerCreateRequest request);
    /// <summary>Destroys a descriptor pool and the descriptor sets allocated from it.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle that owns the pool.</param>
    /// <param name="poolHandle">The native <c>VkDescriptorPool</c> handle to destroy.</param>
    void DestroyPool(nint deviceHandle, nint poolHandle);
    /// <summary>Destroys a sampler.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle that owns the sampler.</param>
    /// <param name="samplerHandle">The native <c>VkSampler</c> handle to destroy.</param>
    void DestroySampler(nint deviceHandle, nint samplerHandle);
    /// <summary>Updates a descriptor set with a buffer binding.</summary>
    /// <param name="request">The write parameters identifying the destination binding and the buffer.</param>
    void WriteBuffer(VulkanDescriptorBufferWriteRequest request);
    /// <summary>Updates a descriptor set with an image (and optional sampler) binding.</summary>
    /// <param name="request">The write parameters identifying the destination binding and the image resources.</param>
    void WriteImage(VulkanDescriptorImageWriteRequest request);
}
