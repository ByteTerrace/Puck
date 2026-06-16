using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Reports the implementation-dependent limits of a physical device (the values embedded in
/// <see cref="VkPhysicalDeviceProperties"/>). This binding carries the prefix of the limits ending at
/// <see cref="TimestampPeriod"/> (see remarks).
/// </summary>
/// <remarks>
/// EXCEPTION (not 1:1): deliberately truncated after TimestampPeriod. The 13 trailing VkPhysicalDeviceLimits fields
/// (maxClipDistances..nonCoherentAtomSize) are omitted because nothing reads them. The driver fills a full-size native
/// buffer (see VulkanNativePhysicalDeviceApi.GetTimestampCapabilities); this typed prefix only needs every present
/// field to keep its exact C offset, which it does.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct VkPhysicalDeviceLimits {
    /// <summary>The maximum dimension of a 1D image.</summary>
    public uint MaxImageDimension1D;
    /// <summary>The maximum dimension of a 2D image.</summary>
    public uint MaxImageDimension2D;
    /// <summary>The maximum dimension of a 3D image.</summary>
    public uint MaxImageDimension3D;
    /// <summary>The maximum dimension of a cube map image.</summary>
    public uint MaxImageDimensionCube;
    /// <summary>The maximum number of layers in an image array.</summary>
    public uint MaxImageArrayLayers;
    /// <summary>The maximum number of texels in a texel buffer.</summary>
    public uint MaxTexelBufferElements;
    /// <summary>The maximum size, in bytes, of a uniform buffer range bound to a descriptor.</summary>
    public uint MaxUniformBufferRange;
    /// <summary>The maximum size, in bytes, of a storage buffer range bound to a descriptor.</summary>
    public uint MaxStorageBufferRange;
    /// <summary>The maximum size, in bytes, of the push constant block.</summary>
    public uint MaxPushConstantsSize;
    /// <summary>The maximum number of device memory allocations that can exist simultaneously.</summary>
    public uint MaxMemoryAllocationCount;
    /// <summary>The maximum number of sampler objects that can be created simultaneously.</summary>
    public uint MaxSamplerAllocationCount;
    /// <summary>The granularity, in bytes, at which buffer and image resources can share an adjacent memory region.</summary>
    public ulong BufferImageGranularity;
    /// <summary>The total size, in bytes, of the sparse memory address space.</summary>
    public ulong SparseAddressSpaceSize;
    /// <summary>The maximum number of descriptor sets that can be bound simultaneously.</summary>
    public uint MaxBoundDescriptorSets;
    /// <summary>The maximum number of sampler descriptors accessible to a single shader stage.</summary>
    public uint MaxPerStageDescriptorSamplers;
    /// <summary>The maximum number of uniform buffer descriptors accessible to a single shader stage.</summary>
    public uint MaxPerStageDescriptorUniformBuffers;
    /// <summary>The maximum number of storage buffer descriptors accessible to a single shader stage.</summary>
    public uint MaxPerStageDescriptorStorageBuffers;
    /// <summary>The maximum number of sampled image descriptors accessible to a single shader stage.</summary>
    public uint MaxPerStageDescriptorSampledImages;
    /// <summary>The maximum number of storage image descriptors accessible to a single shader stage.</summary>
    public uint MaxPerStageDescriptorStorageImages;
    /// <summary>The maximum number of input attachment descriptors accessible to a single shader stage.</summary>
    public uint MaxPerStageDescriptorInputAttachments;
    /// <summary>The maximum number of resources accessible to a single shader stage across all descriptor types.</summary>
    public uint MaxPerStageResources;
    /// <summary>The maximum number of sampler descriptors in a descriptor set.</summary>
    public uint MaxDescriptorSetSamplers;
    /// <summary>The maximum number of uniform buffer descriptors in a descriptor set.</summary>
    public uint MaxDescriptorSetUniformBuffers;
    /// <summary>The maximum number of dynamic uniform buffer descriptors in a descriptor set.</summary>
    public uint MaxDescriptorSetUniformBuffersDynamic;
    /// <summary>The maximum number of storage buffer descriptors in a descriptor set.</summary>
    public uint MaxDescriptorSetStorageBuffers;
    /// <summary>The maximum number of dynamic storage buffer descriptors in a descriptor set.</summary>
    public uint MaxDescriptorSetStorageBuffersDynamic;
    /// <summary>The maximum number of sampled image descriptors in a descriptor set.</summary>
    public uint MaxDescriptorSetSampledImages;
    /// <summary>The maximum number of storage image descriptors in a descriptor set.</summary>
    public uint MaxDescriptorSetStorageImages;
    /// <summary>The maximum number of input attachment descriptors in a descriptor set.</summary>
    public uint MaxDescriptorSetInputAttachments;
    /// <summary>The maximum number of vertex input attributes.</summary>
    public uint MaxVertexInputAttributes;
    /// <summary>The maximum number of vertex input bindings.</summary>
    public uint MaxVertexInputBindings;
    /// <summary>The maximum byte offset of a vertex input attribute relative to the start of its binding's element.</summary>
    public uint MaxVertexInputAttributeOffset;
    /// <summary>The maximum byte stride of a vertex input binding.</summary>
    public uint MaxVertexInputBindingStride;
    /// <summary>The maximum number of output components produced by the vertex shader stage.</summary>
    public uint MaxVertexOutputComponents;
    /// <summary>The maximum tessellation generation level.</summary>
    public uint MaxTessellationGenerationLevel;
    /// <summary>The maximum number of vertices in a tessellation patch.</summary>
    public uint MaxTessellationPatchSize;
    /// <summary>The maximum number of per-vertex input components of the tessellation control shader stage.</summary>
    public uint MaxTessellationControlPerVertexInputComponents;
    /// <summary>The maximum number of per-vertex output components of the tessellation control shader stage.</summary>
    public uint MaxTessellationControlPerVertexOutputComponents;
    /// <summary>The maximum number of per-patch output components of the tessellation control shader stage.</summary>
    public uint MaxTessellationControlPerPatchOutputComponents;
    /// <summary>The maximum total number of output components of the tessellation control shader stage.</summary>
    public uint MaxTessellationControlTotalOutputComponents;
    /// <summary>The maximum number of input components of the tessellation evaluation shader stage.</summary>
    public uint MaxTessellationEvaluationInputComponents;
    /// <summary>The maximum number of output components of the tessellation evaluation shader stage.</summary>
    public uint MaxTessellationEvaluationOutputComponents;
    /// <summary>The maximum number of invocations of the geometry shader per input primitive.</summary>
    public uint MaxGeometryShaderInvocations;
    /// <summary>The maximum number of input components of the geometry shader stage.</summary>
    public uint MaxGeometryInputComponents;
    /// <summary>The maximum number of output components of the geometry shader stage.</summary>
    public uint MaxGeometryOutputComponents;
    /// <summary>The maximum number of vertices the geometry shader may emit.</summary>
    public uint MaxGeometryOutputVertices;
    /// <summary>The maximum total number of output components across all vertices emitted by the geometry shader.</summary>
    public uint MaxGeometryTotalOutputComponents;
    /// <summary>The maximum number of input components of the fragment shader stage.</summary>
    public uint MaxFragmentInputComponents;
    /// <summary>The maximum number of output attachments of the fragment shader stage.</summary>
    public uint MaxFragmentOutputAttachments;
    /// <summary>The maximum number of output attachments usable with dual-source blending.</summary>
    public uint MaxFragmentDualSrcAttachments;
    /// <summary>The maximum combined number of storage buffers, storage images, and color attachments writable from the fragment shader stage.</summary>
    public uint MaxFragmentCombinedOutputResources;
    /// <summary>The maximum size, in bytes, of compute shared memory.</summary>
    public uint MaxComputeSharedMemorySize;
    /// <summary>The maximum number of compute work groups that can be dispatched in each of the x, y, and z dimensions.</summary>
    public fixed uint MaxComputeWorkGroupCount[3];
    /// <summary>The maximum total number of compute shader invocations in a single work group.</summary>
    public uint MaxComputeWorkGroupInvocations;
    /// <summary>The maximum size of a compute work group in each of the x, y, and z dimensions.</summary>
    public fixed uint MaxComputeWorkGroupSize[3];
    /// <summary>The number of bits of subpixel precision in framebuffer coordinates.</summary>
    public uint SubPixelPrecisionBits;
    /// <summary>The number of bits of precision in the division of texel coordinates during sampling.</summary>
    public uint SubTexelPrecisionBits;
    /// <summary>The number of bits of precision in the division used to select a mipmap level during sampling.</summary>
    public uint MipmapPrecisionBits;
    /// <summary>The maximum index value usable for indexed draw calls (excluding the primitive restart index).</summary>
    public uint MaxDrawIndexedIndexValue;
    /// <summary>The maximum draw count supported by indirect draw calls.</summary>
    public uint MaxDrawIndirectCount;
    /// <summary>The maximum absolute sampler level-of-detail bias.</summary>
    public float MaxSamplerLodBias;
    /// <summary>The maximum degree of sampler anisotropy.</summary>
    public float MaxSamplerAnisotropy;
    /// <summary>The maximum number of active viewports.</summary>
    public uint MaxViewports;
    /// <summary>The maximum viewport width and height, in pixels.</summary>
    public fixed uint MaxViewportDimensions[2];
    /// <summary>The inclusive minimum and maximum bounds of a viewport's coordinate range.</summary>
    public fixed float ViewportBoundsRange[2];
    /// <summary>The number of bits of subpixel precision in viewport bounds.</summary>
    public uint ViewportSubPixelBits;
    /// <summary>The minimum required alignment, in bytes, of a pointer returned by mapping memory.</summary>
    public nuint MinMemoryMapAlignment;
    /// <summary>The minimum required alignment, in bytes, of a texel buffer's offset.</summary>
    public ulong MinTexelBufferOffsetAlignment;
    /// <summary>The minimum required alignment, in bytes, of a uniform buffer's offset.</summary>
    public ulong MinUniformBufferOffsetAlignment;
    /// <summary>The minimum required alignment, in bytes, of a storage buffer's offset.</summary>
    public ulong MinStorageBufferOffsetAlignment;
    /// <summary>The minimum (most negative) texel offset usable with image sampling.</summary>
    public int MinTexelOffset;
    /// <summary>The maximum texel offset usable with image sampling.</summary>
    public uint MaxTexelOffset;
    /// <summary>The minimum (most negative) texel offset usable with texture gather operations.</summary>
    public int MinTexelGatherOffset;
    /// <summary>The maximum texel offset usable with texture gather operations.</summary>
    public uint MaxTexelGatherOffset;
    /// <summary>The minimum (most negative) interpolation offset usable with interpolation-at-offset functions.</summary>
    public float MinInterpolationOffset;
    /// <summary>The maximum interpolation offset usable with interpolation-at-offset functions.</summary>
    public float MaxInterpolationOffset;
    /// <summary>The number of bits of subpixel precision in interpolation offsets.</summary>
    public uint SubPixelInterpolationOffsetBits;
    /// <summary>The maximum width, in pixels, of a framebuffer.</summary>
    public uint MaxFramebufferWidth;
    /// <summary>The maximum height, in pixels, of a framebuffer.</summary>
    public uint MaxFramebufferHeight;
    /// <summary>The maximum number of layers in a framebuffer.</summary>
    public uint MaxFramebufferLayers;
    /// <summary>A bitmask of <c>VkSampleCountFlagBits</c> supported for color attachments of a framebuffer.</summary>
    public uint FramebufferColorSampleCounts;
    /// <summary>A bitmask of <c>VkSampleCountFlagBits</c> supported for depth attachments of a framebuffer.</summary>
    public uint FramebufferDepthSampleCounts;
    /// <summary>A bitmask of <c>VkSampleCountFlagBits</c> supported for stencil attachments of a framebuffer.</summary>
    public uint FramebufferStencilSampleCounts;
    /// <summary>A bitmask of <c>VkSampleCountFlagBits</c> supported for a framebuffer with no attachments.</summary>
    public uint FramebufferNoAttachmentsSampleCounts;
    /// <summary>The maximum number of color attachments usable in a subpass.</summary>
    public uint MaxColorAttachments;
    /// <summary>A bitmask of <c>VkSampleCountFlagBits</c> supported by non-integer-format sampled color images.</summary>
    public uint SampledImageColorSampleCounts;
    /// <summary>A bitmask of <c>VkSampleCountFlagBits</c> supported by integer-format sampled images.</summary>
    public uint SampledImageIntegerSampleCounts;
    /// <summary>A bitmask of <c>VkSampleCountFlagBits</c> supported by sampled depth images.</summary>
    public uint SampledImageDepthSampleCounts;
    /// <summary>A bitmask of <c>VkSampleCountFlagBits</c> supported by sampled stencil images.</summary>
    public uint SampledImageStencilSampleCounts;
    /// <summary>A bitmask of <c>VkSampleCountFlagBits</c> supported by storage images.</summary>
    public uint StorageImageSampleCounts;
    /// <summary>The maximum number of 32-bit words in a sample mask.</summary>
    public uint MaxSampleMaskWords;
    /// <summary>A <c>VkBool32</c>; whether timestamps are supported on compute and graphics queues.</summary>
    public uint TimestampComputeAndGraphics;
    /// <summary>The number of nanoseconds a timestamp value is incremented by per tick.</summary>
    public float TimestampPeriod;
}
