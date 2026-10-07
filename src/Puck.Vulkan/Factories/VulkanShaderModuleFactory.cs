using Puck.Shaders;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan.Factories;

/// <summary>
/// The default <see cref="IVulkanShaderModuleFactory"/>: it creates a shader module from the SPIR-V code in
/// the supplied stage information and returns an owning <see cref="VulkanShaderModule"/>, first refusing a module that
/// declares a capability the device was not created with (<see cref="VulkanShaderCapabilities"/>).
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

        VulkanShaderCapabilities.Require(
            module: stageInfo.Path,
            spirv: spirVBytes.Span
        );
        var request = new VulkanShaderModuleCreateRequest(
            Device: logicalDevice.Commands,
            SpirVBytes: spirVBytes
        );
        var result = m_shaderModuleApi.CreateShaderModule(
            moduleHandle: out var moduleHandle,
            request: request
        );

        result.ThrowIfFailed(device: logicalDevice.Commands, operation: "vkCreateShaderModule");

        if (0 == moduleHandle) {
            throw new InvalidOperationException(message: "vkCreateShaderModule returned success without a valid shader-module handle.");
        }

        return new(
            device: logicalDevice.Commands,
            handle: moduleHandle,
            shaderModuleApi: m_shaderModuleApi,
            stage: stageInfo.Stage
        );
    }
}
