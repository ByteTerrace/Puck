using Puck.Shaders;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan.Factories;

/// <summary>
/// The default <see cref="IVulkanShaderModuleFactory"/>: it creates a shader module from the SPIR-V code in
/// the supplied stage information and returns an owning <see cref="VulkanShaderModule"/>.
/// </summary>
public sealed class VulkanShaderModuleFactory : IVulkanShaderModuleFactory {
    private readonly IVulkanShaderModuleApi m_shaderModuleApi;

    /// <summary>Initializes a new instance of the <see cref="VulkanShaderModuleFactory"/> class.</summary>
    /// <param name="shaderModuleApi">The shader-module API used to create and own the underlying module.</param>
    /// <exception cref="ArgumentNullException"><paramref name="shaderModuleApi"/> is <see langword="null"/>.</exception>
    public VulkanShaderModuleFactory(IVulkanShaderModuleApi shaderModuleApi) {
        ArgumentNullException.ThrowIfNull(argument: shaderModuleApi);

        m_shaderModuleApi = shaderModuleApi;
    }

    /// <inheritdoc/>
    public VulkanShaderModule Create(
        ShaderStageInfo stageInfo,
        VulkanLogicalDevice logicalDevice
    ) {
        ArgumentNullException.ThrowIfNull(argument: logicalDevice);

        var spirVBytes = stageInfo.Content;
        var request = new VulkanShaderModuleCreateRequest(
            DeviceHandle: logicalDevice.Handle,
            SpirVBytes: spirVBytes
        );
        var result = m_shaderModuleApi.CreateShaderModule(
            moduleHandle: out var moduleHandle,
            request: request
        );

        result.ThrowIfFailed(operation: "vkCreateShaderModule");

        if (0 == moduleHandle) {
            throw new InvalidOperationException(message: "vkCreateShaderModule returned success without a valid shader-module handle.");
        }

        return new(
            deviceHandle: logicalDevice.Handle,
            handle: moduleHandle,
            shaderModuleApi: m_shaderModuleApi,
            stage: stageInfo.Stage
        );
    }
}
