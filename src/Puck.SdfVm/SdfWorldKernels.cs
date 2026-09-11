namespace Puck.SdfVm;

/// <summary>
/// The compiled compute kernels of the SDF world pipeline. Frame upload precedes sky, instance-mask culling,
/// beam cone marching, indirect-argument generation, primary traversal, views shading, and composition. Views has
/// full, folds, and core variants selected from the program's operations by <see cref="SdfViewsKernelVariants"/>. Brick baking and
/// upload run separately when requested. One backend's set uses SPIR-V for Vulkan or DXIL for Direct3D 12;
/// <see cref="Load(string)"/> reads the selected extension from the deployed assets.
/// </summary>
/// <param name="Sky">The sky pre-pass kernel.</param>
/// <param name="Beam">The tile-cull prepass kernel.</param>
/// <param name="InstanceCull">The per-tile instance-mask kernel.</param>
/// <param name="CullArgs">The cull-args reduction kernel.</param>
/// <param name="Primary">The primary traversal kernel, writing hit records for the views pass.</param>
/// <param name="Views">The per-view shading kernel (the full-ISA reference variant).</param>
/// <param name="ViewsCore">The shading core-ops variant (exotic op/shape cases compiled out).</param>
/// <param name="ViewsFolds">The shading fold-ops variant (folds/scopes kept, the heavy warp/noise family compiled out).</param>
/// <param name="Composite">The Stage 2 source-agnostic compositor kernel.</param>
/// <param name="BrickBake">The standalone carve-union brick baker (<c>sdf-brick-bake.comp</c>) — dispatched only when
/// the engine provisions a brick pool.</param>
/// <param name="BrickUpload">The host-baked brick uploader (<c>sdf-brick-upload.comp</c>) — a staging-to-pool copy, dispatched
/// only when the engine provisions a brick pool.</param>
/// <param name="FrameUpload">The per-frame table uploader (<c>sdf-frame-upload.comp</c>) — copies this frame's host-written
/// viewport rows, dynamic transforms, and frame instance grid into their device-local twins at the top of every frame,
/// so no march kernel reads a host-visible buffer per sample.</param>
public readonly record struct SdfWorldKernels(
    ReadOnlyMemory<byte> Sky,
    ReadOnlyMemory<byte> Beam,
    ReadOnlyMemory<byte> InstanceCull,
    ReadOnlyMemory<byte> CullArgs,
    ReadOnlyMemory<byte> Primary,
    ReadOnlyMemory<byte> Views,
    ReadOnlyMemory<byte> ViewsCore,
    ReadOnlyMemory<byte> ViewsFolds,
    ReadOnlyMemory<byte> Composite,
    ReadOnlyMemory<byte> BrickBake,
    ReadOnlyMemory<byte> BrickUpload,
    ReadOnlyMemory<byte> FrameUpload
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
            BrickUpload: File.ReadAllBytes(path: Path.Combine(path1: directory, path2: $"sdf-brick-upload.comp{bytecodeExtension}")),
            Composite: File.ReadAllBytes(path: Path.Combine(path1: directory, path2: $"sdf-world-composite.comp{bytecodeExtension}")),
            CullArgs: File.ReadAllBytes(path: Path.Combine(path1: directory, path2: $"sdf-cull-args.comp{bytecodeExtension}")),
            FrameUpload: File.ReadAllBytes(path: Path.Combine(path1: directory, path2: $"sdf-frame-upload.comp{bytecodeExtension}")),
            InstanceCull: File.ReadAllBytes(path: Path.Combine(path1: directory, path2: $"sdf-instance-cull.comp{bytecodeExtension}")),
            Primary: File.ReadAllBytes(path: Path.Combine(path1: directory, path2: $"sdf-world-primary.comp{bytecodeExtension}")),
            Sky: File.ReadAllBytes(path: Path.Combine(path1: directory, path2: $"sdf-sky.comp{bytecodeExtension}")),
            Views: File.ReadAllBytes(path: Path.Combine(path1: directory, path2: $"sdf-world-views.comp{bytecodeExtension}")),
            ViewsCore: File.ReadAllBytes(path: Path.Combine(path1: directory, path2: $"sdf-world-views-core.comp{bytecodeExtension}")),
            ViewsFolds: File.ReadAllBytes(path: Path.Combine(path1: directory, path2: $"sdf-world-views-folds.comp{bytecodeExtension}"))
        );
    }
}
