namespace Puck.Shaders;

/// <summary>The compile-time capability floor a shader package's bytecode is built against (<see cref="ShaderPackageCapabilities.TargetFloor"/>).</summary>
/// <param name="Vulkan">The Vulkan API version (e.g. <c>"1.3"</c>).</param>
/// <param name="ShaderModel">The Direct3D shader model (e.g. <c>"6.6"</c>).</param>
public sealed record ShaderTargetFloor(string Vulkan, string ShaderModel);
