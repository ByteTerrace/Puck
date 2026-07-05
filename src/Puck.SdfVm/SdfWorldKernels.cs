namespace Puck.SdfVm;

/// <summary>
/// The four compiled compute kernels of the SDF world pipeline, in chain order: <c>sdf-beam.comp</c> (tile-cull
/// cone-march prepass), <c>sdf-cull-args.comp</c> (GPU-written INDIRECT dispatch args: the surviving-tile bbox),
/// <c>sdf-world-views.comp</c> (per-view render, dispatched indirectly from those args), and
/// <c>sdf-world-composite.comp</c> (source-agnostic region composite). One backend's set — SPIR-V for Vulkan, DXIL
/// for Direct3D 12; <see cref="Load(string)"/> reads whichever the extension selects from the deployed assets.
/// </summary>
/// <param name="Beam">The tile-cull prepass kernel.</param>
/// <param name="CullArgs">The cull-args reduction kernel.</param>
/// <param name="Views">The Stage 1 per-view SDF kernel.</param>
/// <param name="Composite">The Stage 2 source-agnostic compositor kernel.</param>
public readonly record struct SdfWorldKernels(
    ReadOnlyMemory<byte> Beam,
    ReadOnlyMemory<byte> CullArgs,
    ReadOnlyMemory<byte> Views,
    ReadOnlyMemory<byte> Composite
) {
    /// <summary>Loads the world kernel set from the standard deploy location (<c>Assets/Shaders/Sdf</c> next to the
    /// application, where the <c>Puck.SdfVm</c> reference copies its committed bytecode).</summary>
    /// <param name="bytecodeExtension">The compiled-kernel extension (<c>".spv"</c> for Vulkan, <c>".dxil"</c> for Direct3D 12).</param>
    /// <returns>The loaded kernel set.</returns>
    public static SdfWorldKernels Load(string bytecodeExtension) =>
        Load(bytecodeExtension: bytecodeExtension, directory: Path.Combine(AppContext.BaseDirectory, "Assets", "Shaders", "Sdf"));
    /// <summary>Loads the world kernel set from an explicit directory (for hosts with a non-standard asset layout).</summary>
    /// <param name="bytecodeExtension">The compiled-kernel extension (<c>".spv"</c> for Vulkan, <c>".dxil"</c> for Direct3D 12).</param>
    /// <param name="directory">The directory holding the compiled <c>sdf-*.comp</c> kernels.</param>
    /// <returns>The loaded kernel set.</returns>
    public static SdfWorldKernels Load(string bytecodeExtension, string directory) {
        ArgumentException.ThrowIfNullOrEmpty(bytecodeExtension);
        ArgumentException.ThrowIfNullOrEmpty(directory);

        return new SdfWorldKernels(
            Beam: File.ReadAllBytes(path: Path.Combine(directory, $"sdf-beam.comp{bytecodeExtension}")),
            Composite: File.ReadAllBytes(path: Path.Combine(directory, $"sdf-world-composite.comp{bytecodeExtension}")),
            CullArgs: File.ReadAllBytes(path: Path.Combine(directory, $"sdf-cull-args.comp{bytecodeExtension}")),
            Views: File.ReadAllBytes(path: Path.Combine(directory, $"sdf-world-views.comp{bytecodeExtension}"))
        );
    }
}
