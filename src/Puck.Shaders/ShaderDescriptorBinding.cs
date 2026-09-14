using Puck.Abstractions.Gpu;

namespace Puck.Shaders;

/// <summary>Describes one source-level Vulkan descriptor and its DirectX dense-register class.</summary>
/// <param name="VulkanBinding">The logical set-0 binding emitted in GLSL.</param>
/// <param name="Kind">The neutral descriptor kind used by the runtime.</param>
/// <param name="Count">The number of consecutive descriptors in an array binding.</param>
public sealed record ShaderDescriptorBinding(
    uint VulkanBinding,
    GpuComputeBindingKind Kind,
    uint Count = 1
);
