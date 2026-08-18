namespace Puck.Shaders;

/// <summary>One authored descriptor binding of a <see cref="ShaderSetManifest"/> — the binding layout an author
/// declares by hand, cross-checked against the pipeline description built for the same set rather than read from
/// a native shader-reflection dependency.</summary>
/// <param name="Kind">The descriptor kind.</param>
/// <param name="VulkanBinding">The Vulkan descriptor-set binding index.</param>
/// <param name="DirectXRegister">The Direct3D 12 shader register (e.g. <c>"t0"</c>, <c>"u1"</c>).</param>
/// <param name="Count">The descriptor-array length at this binding.</param>
public sealed record ShaderSetManifestBinding(
    ShaderSetManifestBindingKind Kind,
    uint VulkanBinding,
    string DirectXRegister,
    uint Count = 1
);
