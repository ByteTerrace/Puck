namespace Puck.Shaders;

/// <summary>The bytecode one stage compiles to: the index of its step in <see cref="ShaderCompiler.StepsOf"/>.</summary>
public enum ShaderTarget {
    /// <summary>SPIR-V, which Vulkan reads.</summary>
    Spirv = 0,
    /// <summary>DXIL, which Direct3D 12 reads.</summary>
    Dxil = 1,
}
