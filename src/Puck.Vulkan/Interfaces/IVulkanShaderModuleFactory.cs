using Puck.Shaders;
using Puck.Vulkan.Interop;

namespace Puck.Vulkan.Interfaces;

/// <summary>
/// Creates a <see cref="VulkanShaderModule"/> from compiled shader stage information supplied by Puck.Shaders.
/// </summary>
public interface IVulkanShaderModuleFactory {
    /// <summary>Creates a shader module from the SPIR-V code carried by the stage information.</summary>
    /// <param name="stageInfo">The shader stage information, including the SPIR-V byte code and entry point.</param>
    /// <param name="logicalDevice">The logical device the shader module is created on.</param>
    /// <returns>A new, owning <see cref="VulkanShaderModule"/>.</returns>
    VulkanShaderModule Create(ShaderStageInfo stageInfo, VulkanLogicalDevice logicalDevice);
}
