using Puck.Vulkan.Bindings;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan.Interfaces;

/// <summary>
/// Records commands into a command buffer: render pass control, graphics and compute pipeline binding and
/// dispatch, draws, push constants, dynamic scissor, and a set of low-level barrier and transfer primitives.
/// </summary>
public interface IVulkanCommandBufferRecordingApi {
    /// <summary>Begins recording into the command buffer named by the request. No usage flags are set, so the recording may be cached and resubmitted across frames.</summary>
    /// <param name="request">The record request identifying the device and command buffer.</param>
    /// <returns>A <see cref="VkResult"/> indicating whether recording began successfully.</returns>
    VkResult BeginCommandBuffer(VulkanCommandBufferRecordRequest request);
    /// <summary>Begins recording into a command buffer. No usage flags are set, so the recording may be cached and resubmitted across frames.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <param name="commandBufferHandle">The native <c>VkCommandBuffer</c> handle to begin recording into.</param>
    /// <returns>A <see cref="VkResult"/> indicating whether recording began successfully.</returns>
    VkResult BeginCommandBuffer(nint deviceHandle, nint commandBufferHandle);
    /// <summary>Binds a single descriptor set at set number 0 for the graphics bind point.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <param name="commandBufferHandle">The native <c>VkCommandBuffer</c> handle the command is recorded into.</param>
    /// <param name="pipelineLayoutHandle">The native <c>VkPipelineLayout</c> handle compatible with the descriptor set.</param>
    /// <param name="descriptorSetHandle">The native <c>VkDescriptorSet</c> handle to bind.</param>
    void BindDescriptorSet(nint deviceHandle, nint commandBufferHandle, nint pipelineLayoutHandle, nint descriptorSetHandle);
    /// <summary>Binds one or more descriptor sets starting at set number 0 for the graphics bind point.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <param name="commandBufferHandle">The native <c>VkCommandBuffer</c> handle the command is recorded into.</param>
    /// <param name="pipelineLayoutHandle">The native <c>VkPipelineLayout</c> handle compatible with the descriptor sets.</param>
    /// <param name="descriptorSetHandles">The native <c>VkDescriptorSet</c> handles to bind, in order from set 0.</param>
    void BindDescriptorSets(nint deviceHandle, nint commandBufferHandle, nint pipelineLayoutHandle, nint[] descriptorSetHandles);
    /// <summary>Binds a pipeline to the graphics bind point.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <param name="commandBufferHandle">The native <c>VkCommandBuffer</c> handle the command is recorded into.</param>
    /// <param name="pipelineHandle">The native <c>VkPipeline</c> handle to bind.</param>
    void BindGraphicsPipeline(nint deviceHandle, nint commandBufferHandle, nint pipelineHandle);
    /// <summary>Binds a vertex buffer at binding number 0.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <param name="commandBufferHandle">The native <c>VkCommandBuffer</c> handle the command is recorded into.</param>
    /// <param name="vertexBufferBinding">The buffer handle and offset to bind.</param>
    void BindVertexBuffer(nint deviceHandle, nint commandBufferHandle, VulkanVertexBufferBinding vertexBufferBinding);
    /// <summary>Records a non-indexed draw.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <param name="commandBufferHandle">The native <c>VkCommandBuffer</c> handle the command is recorded into.</param>
    /// <param name="vertexCount">The number of vertices to draw.</param>
    /// <param name="instanceCount">The number of instances to draw.</param>
    /// <param name="firstVertex">The index of the first vertex to draw.</param>
    /// <param name="firstInstance">The instance ID of the first instance to draw.</param>
    void Draw(nint deviceHandle, nint commandBufferHandle, uint vertexCount, uint instanceCount, uint firstVertex, uint firstInstance);
    /// <summary>Ends recording of a command buffer.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <param name="commandBufferHandle">The native <c>VkCommandBuffer</c> handle to finish recording.</param>
    /// <returns>A <see cref="VkResult"/> indicating whether recording ended successfully.</returns>
    VkResult EndCommandBuffer(nint deviceHandle, nint commandBufferHandle);
    /// <summary>Records the end of the current render pass instance.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <param name="commandBufferHandle">The native <c>VkCommandBuffer</c> handle the command is recorded into.</param>
    void EndRenderPass(nint deviceHandle, nint commandBufferHandle);
    /// <summary>Records an update of a range of the push constant block.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <param name="commandBufferHandle">The native <c>VkCommandBuffer</c> handle the command is recorded into.</param>
    /// <param name="pipelineLayoutHandle">The native <c>VkPipelineLayout</c> handle declaring the push constant range.</param>
    /// <param name="stageFlags">A bitmask of <c>VkShaderStageFlagBits</c> identifying the stages that consume the updated constants.</param>
    /// <param name="offset">The start offset, in bytes, of the range to update.</param>
    /// <param name="data">The constant data to write.</param>
    void PushConstants(
        nint deviceHandle,
        nint commandBufferHandle,
        nint pipelineLayoutHandle,
        uint stageFlags,
        uint offset,
        ReadOnlySpan<byte> data
    );
    /// <summary>Records a dynamic scissor rectangle for viewport 0.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <param name="commandBufferHandle">The native <c>VkCommandBuffer</c> handle the command is recorded into.</param>
    /// <param name="x">The x coordinate of the scissor rectangle's upper-left corner.</param>
    /// <param name="y">The y coordinate of the scissor rectangle's upper-left corner.</param>
    /// <param name="width">The width, in pixels, of the scissor rectangle.</param>
    /// <param name="height">The height, in pixels, of the scissor rectangle.</param>
    void SetScissor(nint deviceHandle, nint commandBufferHandle, int x, int y, uint width, uint height);
    /// <summary>Begins a render pass instance for the request's framebuffer and render pass, clearing the color attachment to opaque black over the full render area, with inline subpass contents.</summary>
    /// <param name="request">The record request identifying the device, command buffer, render pass, framebuffer, and render area.</param>
    void StartRenderPass(VulkanCommandBufferRecordRequest request);
    /// <summary>Binds a pipeline to the compute bind point.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <param name="commandBufferHandle">The native <c>VkCommandBuffer</c> handle the command is recorded into.</param>
    /// <param name="pipelineHandle">The native <c>VkPipeline</c> handle to bind.</param>
    void BindComputePipeline(nint deviceHandle, nint commandBufferHandle, nint pipelineHandle);
    /// <summary>Binds one or more descriptor sets starting at set number 0 for the compute bind point.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <param name="commandBufferHandle">The native <c>VkCommandBuffer</c> handle the command is recorded into.</param>
    /// <param name="pipelineLayoutHandle">The native <c>VkPipelineLayout</c> handle compatible with the descriptor sets.</param>
    /// <param name="descriptorSetHandles">The native <c>VkDescriptorSet</c> handles to bind, in order from set 0.</param>
    void BindComputeDescriptorSets(nint deviceHandle, nint commandBufferHandle, nint pipelineLayoutHandle, ReadOnlySpan<nint> descriptorSetHandles);
    /// <summary>Records a compute dispatch.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <param name="commandBufferHandle">The native <c>VkCommandBuffer</c> handle the command is recorded into.</param>
    /// <param name="groupCountX">The number of local work groups dispatched in the x dimension.</param>
    /// <param name="groupCountY">The number of local work groups dispatched in the y dimension.</param>
    /// <param name="groupCountZ">The number of local work groups dispatched in the z dimension.</param>
    void Dispatch(nint deviceHandle, nint commandBufferHandle, uint groupCountX, uint groupCountY, uint groupCountZ);
    /// <summary>Records an indirect compute dispatch (<c>vkCmdDispatchIndirect</c>): the group counts are read from a <c>VkDispatchIndirectCommand</c> in the buffer.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <param name="commandBufferHandle">The native <c>VkCommandBuffer</c> handle the command is recorded into.</param>
    /// <param name="bufferHandle">The native <c>VkBuffer</c> handle holding the group counts (must carry <c>VK_BUFFER_USAGE_INDIRECT_BUFFER_BIT</c>).</param>
    /// <param name="offset">The byte offset into <paramref name="bufferHandle"/> of the <c>VkDispatchIndirectCommand</c>.</param>
    void DispatchIndirect(nint deviceHandle, nint commandBufferHandle, nint bufferHandle, ulong offset);

    // Low-level synchronization and transfer primitives: no barriers are implied, so the
    // caller sequences layouts and access/stage masks. All operate on 2D, single-layer,
    // color images.

    /// <summary>Records a pipeline barrier that transitions a range of mip levels of a 2D, single-layer color image between layouts. No surrounding barriers are implied; the caller supplies the access and stage scopes.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <param name="commandBufferHandle">The native <c>VkCommandBuffer</c> handle the command is recorded into.</param>
    /// <param name="imageHandle">The native <c>VkImage</c> handle to transition.</param>
    /// <param name="baseMipLevel">The first mip level affected.</param>
    /// <param name="mipLevelCount">The number of mip levels affected, starting from <paramref name="baseMipLevel"/>.</param>
    /// <param name="oldLayout">The current layout of the image, as a <c>VkImageLayout</c> value.</param>
    /// <param name="newLayout">The layout to transition to, as a <c>VkImageLayout</c> value.</param>
    /// <param name="sourceAccessMask">A bitmask of <c>VkAccessFlagBits</c> giving the source access scope.</param>
    /// <param name="destinationAccessMask">A bitmask of <c>VkAccessFlagBits</c> giving the destination access scope.</param>
    /// <param name="sourceStageMask">A bitmask of <c>VkPipelineStageFlagBits</c> giving the source stage scope.</param>
    /// <param name="destinationStageMask">A bitmask of <c>VkPipelineStageFlagBits</c> giving the destination stage scope.</param>
    void TransitionImageLayout(
        nint deviceHandle,
        nint commandBufferHandle,
        nint imageHandle,
        uint baseMipLevel,
        uint mipLevelCount,
        uint oldLayout,
        uint newLayout,
        uint sourceAccessMask,
        uint destinationAccessMask,
        uint sourceStageMask,
        uint destinationStageMask
    );
    /// <summary>Records a global memory barrier over the given access and stage scopes, without reference to a specific resource.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <param name="commandBufferHandle">The native <c>VkCommandBuffer</c> handle the command is recorded into.</param>
    /// <param name="sourceAccessMask">A bitmask of <c>VkAccessFlagBits</c> giving the source access scope.</param>
    /// <param name="destinationAccessMask">A bitmask of <c>VkAccessFlagBits</c> giving the destination access scope.</param>
    /// <param name="sourceStageMask">A bitmask of <c>VkPipelineStageFlagBits</c> giving the source stage scope.</param>
    /// <param name="destinationStageMask">A bitmask of <c>VkPipelineStageFlagBits</c> giving the destination stage scope.</param>
    void PipelineMemoryBarrier(
        nint deviceHandle,
        nint commandBufferHandle,
        uint sourceAccessMask,
        uint destinationAccessMask,
        uint sourceStageMask,
        uint destinationStageMask
    );
    /// <summary>Records a clear of a 2D, single-layer color image to the given RGBA color.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <param name="commandBufferHandle">The native <c>VkCommandBuffer</c> handle the command is recorded into.</param>
    /// <param name="imageHandle">The native <c>VkImage</c> handle to clear.</param>
    /// <param name="imageLayout">The current layout of the image, as a <c>VkImageLayout</c> value.</param>
    /// <param name="red">The red component of the clear color.</param>
    /// <param name="green">The green component of the clear color.</param>
    /// <param name="blue">The blue component of the clear color.</param>
    /// <param name="alpha">The alpha component of the clear color.</param>
    void ClearColorImage(
        nint deviceHandle,
        nint commandBufferHandle,
        nint imageHandle,
        uint imageLayout,
        float red,
        float green,
        float blue,
        float alpha
    );
    /// <summary>Records a copy of a width × height region between two 2D, single-layer color images, from origin to origin.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <param name="commandBufferHandle">The native <c>VkCommandBuffer</c> handle the command is recorded into.</param>
    /// <param name="sourceImageHandle">The native <c>VkImage</c> handle to copy from.</param>
    /// <param name="sourceImageLayout">The current layout of the source image, as a <c>VkImageLayout</c> value.</param>
    /// <param name="destinationImageHandle">The native <c>VkImage</c> handle to copy to.</param>
    /// <param name="destinationImageLayout">The current layout of the destination image, as a <c>VkImageLayout</c> value.</param>
    /// <param name="width">The width, in texels, of the region to copy.</param>
    /// <param name="height">The height, in texels, of the region to copy.</param>
    void CopyImageToImage(
        nint deviceHandle,
        nint commandBufferHandle,
        nint sourceImageHandle,
        uint sourceImageLayout,
        nint destinationImageHandle,
        uint destinationImageLayout,
        uint width,
        uint height
    );
    /// <summary>Records a copy of a width × height region of a 2D, single-layer color image into a tightly packed buffer.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <param name="commandBufferHandle">The native <c>VkCommandBuffer</c> handle the command is recorded into.</param>
    /// <param name="imageHandle">The native <c>VkImage</c> handle to copy from.</param>
    /// <param name="imageLayout">The current layout of the image, as a <c>VkImageLayout</c> value.</param>
    /// <param name="bufferHandle">The native <c>VkBuffer</c> handle to copy into.</param>
    /// <param name="width">The width, in texels, of the region to copy.</param>
    /// <param name="height">The height, in texels, of the region to copy.</param>
    void CopyImageToBuffer(
        nint deviceHandle,
        nint commandBufferHandle,
        nint imageHandle,
        uint imageLayout,
        nint bufferHandle,
        uint width,
        uint height
    );
    /// <summary>Records a copy from a tightly packed buffer into a width × height region of a 2D, single-layer color image at the given offset.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <param name="commandBufferHandle">The native <c>VkCommandBuffer</c> handle the command is recorded into.</param>
    /// <param name="bufferHandle">The native <c>VkBuffer</c> handle to copy from.</param>
    /// <param name="imageHandle">The native <c>VkImage</c> handle to copy to.</param>
    /// <param name="imageLayout">The current layout of the image, as a <c>VkImageLayout</c> value.</param>
    /// <param name="imageOffsetX">The x texel offset of the destination region.</param>
    /// <param name="imageOffsetY">The y texel offset of the destination region.</param>
    /// <param name="width">The width, in texels, of the region to copy.</param>
    /// <param name="height">The height, in texels, of the region to copy.</param>
    void CopyBufferToImage(
        nint deviceHandle,
        nint commandBufferHandle,
        nint bufferHandle,
        nint imageHandle,
        uint imageLayout,
        int imageOffsetX,
        int imageOffsetY,
        uint width,
        uint height
    );
    /// <summary>Records a (possibly scaling) blit of a region between mip levels of two 2D, single-layer color images.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <param name="commandBufferHandle">The native <c>VkCommandBuffer</c> handle the command is recorded into.</param>
    /// <param name="sourceImageHandle">The native <c>VkImage</c> handle to blit from.</param>
    /// <param name="sourceImageLayout">The current layout of the source image, as a <c>VkImageLayout</c> value.</param>
    /// <param name="sourceMipLevel">The mip level of the source image to read.</param>
    /// <param name="sourceWidth">The width, in texels, of the source region.</param>
    /// <param name="sourceHeight">The height, in texels, of the source region.</param>
    /// <param name="destinationImageHandle">The native <c>VkImage</c> handle to blit to.</param>
    /// <param name="destinationImageLayout">The current layout of the destination image, as a <c>VkImageLayout</c> value.</param>
    /// <param name="destinationMipLevel">The mip level of the destination image to write.</param>
    /// <param name="destinationWidth">The width, in texels, of the destination region.</param>
    /// <param name="destinationHeight">The height, in texels, of the destination region.</param>
    /// <param name="filter">The filter applied when the regions differ in size, as a <c>VkFilter</c> value.</param>
    void BlitImage(
        nint deviceHandle,
        nint commandBufferHandle,
        nint sourceImageHandle,
        uint sourceImageLayout,
        uint sourceMipLevel,
        uint sourceWidth,
        uint sourceHeight,
        nint destinationImageHandle,
        uint destinationImageLayout,
        uint destinationMipLevel,
        uint destinationWidth,
        uint destinationHeight,
        uint filter
    );
}
