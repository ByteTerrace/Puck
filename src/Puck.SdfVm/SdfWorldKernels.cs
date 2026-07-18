namespace Puck.SdfVm;

/// <summary>
/// The compiled compute kernels of the SDF world pipeline, in chain order: <c>sdf-beam.comp</c> (tile-cull
/// cone-march prepass), <c>sdf-instance-cull.comp</c> (the per-tile instance-mask pass — its own kernel so its cell
/// walk's register footprint never taxes the cone march's occupancy), <c>sdf-cull-args.comp</c> (GPU-written INDIRECT
/// dispatch args: the surviving-tile bbox), <c>sdf-world-views.comp</c> (per-view render, dispatched indirectly from
/// those args) plus its core-ops compiled variant <c>sdf-world-views-core.comp</c> (the exotic-ISA strip
/// <see cref="SdfWorldEngine.UploadProgram"/> selects per program — see <see cref="SdfViewsKernelVariant"/>), and
/// <c>sdf-world-composite.comp</c> (source-agnostic region composite). One backend's set — SPIR-V
/// for Vulkan, DXIL for Direct3D 12; <see cref="Load(string)"/> reads whichever the extension selects from the
/// deployed assets.
/// </summary>
/// <param name="Beam">The tile-cull prepass kernel.</param>
/// <param name="InstanceCull">The per-tile instance-mask kernel.</param>
/// <param name="CullArgs">The cull-args reduction kernel.</param>
/// <param name="Views">The Stage 1 per-view SDF kernel (the full-ISA reference variant).</param>
/// <param name="ViewsCore">The Stage 1 core-ops variant (exotic op/shape cases compiled out).</param>
/// <param name="Composite">The Stage 2 source-agnostic compositor kernel.</param>
/// <param name="BrickBake">The standalone carve-union brick baker (<c>sdf-brick-bake.comp</c>) — dispatched only when
/// the engine provisions a brick pool (carve-bake plan §3).</param>
public readonly record struct SdfWorldKernels(
    ReadOnlyMemory<byte> Beam,
    ReadOnlyMemory<byte> InstanceCull,
    ReadOnlyMemory<byte> CullArgs,
    ReadOnlyMemory<byte> Views,
    ReadOnlyMemory<byte> ViewsCore,
    ReadOnlyMemory<byte> Composite,
    ReadOnlyMemory<byte> BrickBake
) {
    /// <summary>The standard deploy location (<c>Assets/Shaders/Sdf</c> next to the application, where the
    /// <c>Puck.SdfVm</c> reference copies its committed bytecode) — <see cref="Load(string)"/>'s default directory,
    /// exposed for callers that load an individual SDF-directory asset directly (e.g. the shared <c>fullscreen.vert</c>
    /// vertex stage a 2D overlay decorator reuses) rather than the whole kernel set.</summary>
    public static string DefaultDirectory => Path.Combine(path1: AppContext.BaseDirectory, path2: "Assets", path3: "Shaders", path4: "Sdf");

    /// <summary>Loads the world kernel set from the standard deploy location (<see cref="DefaultDirectory"/>).</summary>
    /// <param name="bytecodeExtension">The compiled-kernel extension (<c>".spv"</c> for Vulkan, <c>".dxil"</c> for Direct3D 12).</param>
    /// <returns>The loaded kernel set.</returns>
    public static SdfWorldKernels Load(string bytecodeExtension) =>
        Load(bytecodeExtension: bytecodeExtension, directory: DefaultDirectory);
    /// <summary>Loads the world kernel set from an explicit directory (for hosts with a non-standard asset layout).</summary>
    /// <param name="bytecodeExtension">The compiled-kernel extension (<c>".spv"</c> for Vulkan, <c>".dxil"</c> for Direct3D 12).</param>
    /// <param name="directory">The directory holding the compiled <c>sdf-*.comp</c> kernels.</param>
    /// <returns>The loaded kernel set.</returns>
    public static SdfWorldKernels Load(string bytecodeExtension, string directory) {
        ArgumentException.ThrowIfNullOrEmpty(bytecodeExtension);
        ArgumentException.ThrowIfNullOrEmpty(directory);

        return new SdfWorldKernels(
            Beam: File.ReadAllBytes(path: Path.Combine(path1: directory, path2: $"sdf-beam.comp{bytecodeExtension}")),
            BrickBake: File.ReadAllBytes(path: Path.Combine(path1: directory, path2: $"sdf-brick-bake.comp{bytecodeExtension}")),
            Composite: File.ReadAllBytes(path: Path.Combine(path1: directory, path2: $"sdf-world-composite.comp{bytecodeExtension}")),
            CullArgs: File.ReadAllBytes(path: Path.Combine(path1: directory, path2: $"sdf-cull-args.comp{bytecodeExtension}")),
            InstanceCull: File.ReadAllBytes(path: Path.Combine(path1: directory, path2: $"sdf-instance-cull.comp{bytecodeExtension}")),
            Views: File.ReadAllBytes(path: Path.Combine(path1: directory, path2: $"sdf-world-views.comp{bytecodeExtension}")),
            ViewsCore: File.ReadAllBytes(path: Path.Combine(path1: directory, path2: $"sdf-world-views-core.comp{bytecodeExtension}"))
        );
    }
}
