namespace Puck.Shaders;

/// <summary>The compile-time capability floor a <see cref="ShaderSetManifest"/>'s bytecode was built against.</summary>
/// <param name="Vulkan">The Vulkan API version (e.g. <c>"1.3"</c>).</param>
/// <param name="ShaderModel">The Direct3D shader model (e.g. <c>"6.6"</c>).</param>
public sealed record ShaderSetManifestTargetFloor(string Vulkan, string ShaderModel);
