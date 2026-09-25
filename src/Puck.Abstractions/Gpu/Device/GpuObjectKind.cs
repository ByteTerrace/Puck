namespace Puck.Abstractions.Gpu;

/// <summary>
/// The kind of native object a backend hands <see cref="GpuObjectNaming.Name"/>, which tells it how to read the handle:
/// Vulkan maps each kind to its <c>VkObjectType</c>, and Direct3D 12 names only the kinds that are an
/// <c>ID3D12Object</c> there.
/// </summary>
public enum GpuObjectKind : byte {
    /// <summary>A buffer: a <c>VkBuffer</c>, or a Direct3D 12 buffer resource.</summary>
    Buffer = 0,
    /// <summary>An image: a <c>VkImage</c>, or a Direct3D 12 texture resource.</summary>
    Image = 1,
    /// <summary>An image's view: a <c>VkImageView</c>. Direct3D 12 views are descriptors, not objects.</summary>
    ImageView = 2,
    /// <summary>A compute or graphics pipeline: a <c>VkPipeline</c>, or an <c>ID3D12PipelineState</c>.</summary>
    Pipeline = 3,
    /// <summary>A descriptor pool: a <c>VkDescriptorPool</c>. A Direct3D 12 pool is a range of the device's heap.</summary>
    DescriptorPool = 4,
    /// <summary>A descriptor set: a <c>VkDescriptorSet</c>. A Direct3D 12 set is a range of its pool.</summary>
    DescriptorSet = 5,
    /// <summary>A command pool: a <c>VkCommandPool</c>, or an <c>ID3D12CommandAllocator</c>.</summary>
    CommandPool = 6,
    /// <summary>A command pool's command buffer: a <c>VkCommandBuffer</c>, or an <c>ID3D12GraphicsCommandList</c>.</summary>
    CommandBuffer = 7,
    /// <summary>A render pass: a <c>VkRenderPass</c>. A Direct3D 12 render pass is recorded state, not an object.</summary>
    RenderPass = 8,
}
