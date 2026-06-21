namespace Puck.Abstractions;

/// <summary>
/// Creates backend-neutral shader modules from pre-validated bytecode.
/// </summary>
public interface IGpuShaderModuleFactory {
    /// <summary>Creates a shader module from bytecode that was previously validated by <c>IShaderModuleLoader</c>.</summary>
    /// <param name="deviceContext">The GPU device context.</param>
    /// <param name="stage">The shader stage (use <see cref="GpuShaderStage"/> constants).</param>
    /// <param name="bytecode">The compiled shader bytecode (SPIR-V, DXIL, etc.).</param>
    /// <returns>A new, owning <see cref="IGpuShaderModule"/>.</returns>
    IGpuShaderModule Create(IGpuDeviceContext deviceContext, uint stage, ReadOnlyMemory<byte> bytecode);
}
