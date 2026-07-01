namespace Puck.Abstractions.Gpu;

/// <summary>
/// Creates backend-neutral shader modules from compiled bytecode.
/// </summary>
public interface IGpuShaderModuleFactory {
    /// <summary>Creates a shader module from compiled bytecode. Both backends first validate the container via
    /// <see cref="ShaderBytecode.ValidateFormat"/> (rejecting anything that is not recognizable SPIR-V or DXBC/DXIL),
    /// then wrap it: Vulkan creates a <c>VkShaderModule</c> from the SPIR-V, while Direct3D 12 pins the DXBC/DXIL
    /// bytes in managed memory so they are directly addressable as a <c>D3D12_SHADER_BYTECODE</c> at
    /// pipeline-creation time.</summary>
    /// <param name="deviceContext">The GPU device context.</param>
    /// <param name="stage">The shader stage (use <see cref="GpuShaderStage"/> constants).</param>
    /// <param name="bytecode">The compiled shader bytecode (SPIR-V for Vulkan; DXBC/DXIL for Direct3D 12).</param>
    /// <returns>A new, owning <see cref="IGpuShaderModule"/> that the caller disposes.</returns>
    /// <exception cref="ArgumentException"><paramref name="bytecode"/> is not a recognizable, well-formed SPIR-V or DXBC/DXIL container.</exception>
    IGpuShaderModule Create(IGpuDeviceContext deviceContext, uint stage, ReadOnlyMemory<byte> bytecode);
}
