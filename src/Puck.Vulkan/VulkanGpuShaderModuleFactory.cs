using Puck.Shaders;
using Puck.Vulkan.Interfaces;

namespace Puck.Vulkan;

/// <summary>
/// Implements <see cref="IGpuShaderModuleFactory"/> by forwarding to <see cref="IVulkanShaderModuleFactory"/>,
/// converting <see cref="GpuShaderStage"/> flags to the <see cref="ShaderStage"/> enum.
/// </summary>
public sealed class VulkanGpuShaderModuleFactory(IVulkanDeviceContext deviceContext, IVulkanShaderModuleFactory shaderModuleFactory) : IGpuShaderModuleFactory {
    private static ShaderStage ToShaderStage(GpuShaderStage stageFlags) => stageFlags switch {
        GpuShaderStage.Vertex => ShaderStage.Vertex,
        GpuShaderStage.Fragment => ShaderStage.Fragment,
        GpuShaderStage.Compute => ShaderStage.Compute,
        _ => throw new ArgumentOutOfRangeException(
        paramName: nameof(stageFlags),
        actualValue: stageFlags,
        message: null
    ),
    };

    /// <inheritdoc/>
    public IGpuShaderModule Create(GpuShaderStage stage, ReadOnlyMemory<byte> bytecode) {
        ShaderBytecode.ValidateFormat(bytecode: bytecode.Span);

        var logicalDevice = deviceContext.LogicalDevice;
        var stageInfo = new ShaderStageInfo(
            ByteLength: bytecode.Length,
            Content: bytecode,
            ContentHash: default,
            Path: string.Empty,
            Stage: ToShaderStage(stageFlags: stage)
        );

        return shaderModuleFactory.Create(
            logicalDevice: logicalDevice,
            stageInfo: stageInfo
        );
    }
}
