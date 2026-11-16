using Puck.Abstractions.Gpu;

namespace Puck.Shaders;

/// <summary>The bytecode one stage compiles to: the index of its step in <see cref="ShaderCompiler.StepsOf"/>.</summary>
public enum ShaderTarget {
    /// <summary>SPIR-V, which Vulkan reads.</summary>
    Spirv = 0,
    /// <summary>DXIL, which Direct3D 12 reads.</summary>
    Dxil = 1,
}

/// <summary>
/// The one mapping between a backend, its compiled-bytecode file extension and a <see cref="ShaderTarget"/>:
/// <see cref="ShaderBytecode"/> owns the extension and the container magic, and these members carry it to the target.
/// </summary>
public static class ShaderTargets {
    /// <summary>Gets the target a backend loads.</summary>
    /// <param name="hostsOnDirectX">Whether the backend is Direct3D 12.</param>
    /// <returns><see cref="ShaderTarget.Dxil"/> for Direct3D 12, otherwise <see cref="ShaderTarget.Spirv"/>.</returns>
    public static ShaderTarget Of(bool hostsOnDirectX) => (hostsOnDirectX
        ? ShaderTarget.Dxil
        : ShaderTarget.Spirv
    );
    /// <summary>Gets the target some compiled bytecode was built for, by its container magic.</summary>
    /// <param name="bytecode">The compiled shader bytecode.</param>
    /// <returns><see cref="ShaderTarget.Dxil"/> when it is a DXBC container, otherwise <see cref="ShaderTarget.Spirv"/>.</returns>
    public static ShaderTarget Of(ReadOnlySpan<byte> bytecode) => Of(hostsOnDirectX: ShaderBytecode.IsDxbcContainer(bytecode: bytecode));
    /// <summary>Gets the compiled-bytecode file extension a target is stored under.</summary>
    /// <param name="target">The target.</param>
    /// <returns>The extension with its leading dot, as <see cref="ShaderBytecode.FileExtension"/> spells it.</returns>
    public static string FileExtension(this ShaderTarget target) => ShaderBytecode.FileExtension(hostsOnDirectX: (target == ShaderTarget.Dxil));
    /// <summary>Gets the path a deployed stage's compiled bytecode for a target is stored at: <c>{stem}.{stage}</c> and the
    /// target's extension, in a directory.</summary>
    /// <param name="target">The target.</param>
    /// <param name="directory">The directory the stages are deployed in.</param>
    /// <param name="stem">The stage's file stem.</param>
    /// <param name="stage">The stage's file suffix, such as <c>comp</c>, <c>vert</c> or <c>frag</c>.</param>
    /// <returns>The bytecode's path.</returns>
    public static string DeployedPath(this ShaderTarget target, string directory, string stem, string stage) => Path.Combine(
        path1: directory,
        path2: $"{stem}.{stage}{target.FileExtension()}"
    );
}
