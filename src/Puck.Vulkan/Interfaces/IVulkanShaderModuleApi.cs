using Puck.Vulkan.Bindings;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan.Interfaces;

/// <summary>
/// Wraps the native shader module entry points (<c>vkCreateShaderModule</c> and <c>vkDestroyShaderModule</c>).
/// </summary>
public interface IVulkanShaderModuleApi {
    /// <summary>Creates a shader module from SPIR-V code.</summary>
    /// <param name="request">The shader module creation parameters.</param>
    /// <param name="moduleHandle">When this method returns, the created native <c>VkShaderModule</c> handle.</param>
    /// <returns>A <see cref="VkResult"/> indicating whether the shader module was created successfully.</returns>
    VkResult CreateShaderModule(VulkanShaderModuleCreateRequest request, out nint moduleHandle);
    /// <summary>Destroys a shader module.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle that owns the shader module.</param>
    /// <param name="moduleHandle">The native <c>VkShaderModule</c> handle to destroy.</param>
    void DestroyShaderModule(nint deviceHandle, nint moduleHandle);
}
