using Puck.Abstractions.Counting;

namespace Puck.SdfVm;

/// <summary>
/// The compiled compute kernels of the SDF world pipeline: sky, instance-mask culling,
/// beam cone marching, indirect-argument generation, primary traversal, surface evaluation, ambient occlusion,
/// views shading, and composition. Views has
/// full, folds, and core variants selected from the program's operations by <see cref="SdfViewsKernelVariants"/>. Brick baking and
/// upload run separately when requested. One backend's set uses SPIR-V for Vulkan or DXIL for Direct3D 12;
/// <see cref="Load(string)"/> reads the selected extension from the deployed assets, and every load is counted into
/// <see cref="LoadWork"/>.
/// </summary>
/// <param name="Sky">The sky pre-pass kernel.</param>
/// <param name="Beam">The tile-cull prepass kernel.</param>
/// <param name="InstanceCull">The per-tile instance-mask kernel.</param>
/// <param name="CullArgs">The cull-args reduction kernel.</param>
/// <param name="Primary">The primary traversal kernel, writing visibility records for the views pass.</param>
/// <param name="Surface">The geometric normal and curvature pass, writing the visibility record's normal and surface rows.</param>
/// <param name="Ambient">The ambient-occlusion pass, reading geometric normals and updating the surface rows.</param>
/// <param name="Views">The per-view shading kernel (the full-ISA reference variant).</param>
/// <param name="ViewsCore">The shading core-ops variant (exotic op/shape cases compiled out).</param>
/// <param name="ViewsFolds">The shading fold-ops variant (folds/scopes kept, the heavy warp/noise family compiled out).</param>
/// <param name="Composite">The Stage 2 source-agnostic compositor kernel.</param>
/// <param name="BrickBake">The standalone carve-union brick baker (<c>sdf-brick-bake.comp</c>) — dispatched only when
/// the engine provisions a brick pool.</param>
public readonly record struct SdfWorldKernels(
    ReadOnlyMemory<byte> Sky,
    ReadOnlyMemory<byte> Beam,
    ReadOnlyMemory<byte> InstanceCull,
    ReadOnlyMemory<byte> CullArgs,
    ReadOnlyMemory<byte> Primary,
    ReadOnlyMemory<byte> Surface,
    ReadOnlyMemory<byte> Ambient,
    ReadOnlyMemory<byte> Views,
    ReadOnlyMemory<byte> ViewsCore,
    ReadOnlyMemory<byte> ViewsFolds,
    ReadOnlyMemory<byte> Composite,
    ReadOnlyMemory<byte> BrickBake
) {
    /// <summary>The name a counters report heads <see cref="LoadWork"/>'s section with.</summary>
    public const string LoadWorkSourceName = "shaders.sdf-kernels";

    /// <summary>The standard deploy location (<c>Assets/Shaders/Sdf</c> next to the application, where the
    /// <c>Puck.SdfVm</c> reference copies the bytecode its build compiles; the bytecode is a build product, never
    /// tracked in git) — <see cref="Load(string)"/>'s default directory,
    /// exposed for callers that load an individual SDF-directory asset directly (e.g. the shared <c>fullscreen.vert</c>
    /// vertex stage a 2D overlay decorator reuses) rather than the whole kernel set.</summary>
    public static string DefaultDirectory => Path.Combine(
        path1: AppContext.BaseDirectory,
        path2: "Assets",
        path3: "Shaders",
        path4: "Sdf"
    );

    /// <summary>Gets the kind counting kernel-set loads: one per <see cref="Load(string, string, WorkCounterSet)"/>
    /// that starts reading, each reading every kernel of one backend.</summary>
    public static WorkKind Loads { get; } = new(name: "shaders.sdf-kernels.loads", unit: "count", workClass: WorkClass.PerBackendDeterministic);
    /// <summary>Gets the kind counting the bytecode bytes those loads read; SPIR-V and DXIL sets differ in size, so it
    /// is compared within one backend only.</summary>
    public static WorkKind BytecodeBytes { get; } = new(name: "shaders.sdf-kernels.bytecode-bytes", unit: "bytes", workClass: WorkClass.PerBackendDeterministic);

    /// <summary>Gets the process's kernel-set load counts, which <see cref="Load(string)"/> and
    /// <see cref="Load(string, string)"/> count into and a host registers as its <see cref="LoadWorkSourceName"/>
    /// source. Counts only go up, and any thread may load.</summary>
    public static WorkCounterSet LoadWork =>
        LoadCounts.Process;

    /// <summary>Returns the kernel set's content key: a hash over every kernel's bytecode, in declaration order. A host
    /// keys its persistent pipeline cache by it (<see cref="Puck.Abstractions.Gpu.GpuPipelineCacheStore"/>), so a
    /// changed kernel starts a new cache file.</summary>
    /// <returns>A lowercase 16-digit hexadecimal key.</returns>
    public string ContentKey() =>
        Puck.Abstractions.Gpu.GpuPipelineCacheStore.ContentKeyOf(parts: [Sky, Beam, InstanceCull, CullArgs, Primary, Surface, Ambient, Views, ViewsCore, ViewsFolds, Composite, BrickBake]);
    /// <summary>Loads the world kernel set from the standard deploy location (<see cref="DefaultDirectory"/>).</summary>
    /// <param name="bytecodeExtension">The compiled-kernel extension (<c>".spv"</c> for Vulkan, <c>".dxil"</c> for Direct3D 12).</param>
    /// <returns>The loaded kernel set.</returns>
    public static SdfWorldKernels Load(string bytecodeExtension) =>
        Load(
            bytecodeExtension: bytecodeExtension,
            directory: DefaultDirectory
        );
    /// <summary>Loads the world kernel set from an explicit directory (for hosts with a non-standard asset layout).</summary>
    /// <param name="bytecodeExtension">The compiled-kernel extension (<c>".spv"</c> for Vulkan, <c>".dxil"</c> for Direct3D 12).</param>
    /// <param name="directory">The directory holding the compiled <c>sdf-*.comp</c> kernels.</param>
    /// <returns>The loaded kernel set.</returns>
    public static SdfWorldKernels Load(string bytecodeExtension, string directory) =>
        Load(
            bytecodeExtension: bytecodeExtension,
            directory: directory,
            work: LoadWork
        );
    /// <summary>Loads the world kernel set from an explicit directory, counting the load and the bytes it read into
    /// <paramref name="work"/> rather than the process's <see cref="LoadWork"/>.</summary>
    /// <param name="bytecodeExtension">The compiled-kernel extension (<c>".spv"</c> for Vulkan, <c>".dxil"</c> for Direct3D 12).</param>
    /// <param name="directory">The directory holding the compiled <c>sdf-*.comp</c> kernels.</param>
    /// <param name="work">The counts the load adds to; it must count <see cref="Loads"/> and <see cref="BytecodeBytes"/>.</param>
    /// <returns>The loaded kernel set.</returns>
    /// <exception cref="ArgumentException"><paramref name="bytecodeExtension"/> or <paramref name="directory"/> is
    /// empty, or <paramref name="work"/> does not count <see cref="Loads"/> and <see cref="BytecodeBytes"/>.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="work"/> is <see langword="null"/>.</exception>
    /// <exception cref="IOException">A kernel file is missing or cannot be read.</exception>
    public static SdfWorldKernels Load(string bytecodeExtension, string directory, WorkCounterSet work) {
        ArgumentException.ThrowIfNullOrEmpty(bytecodeExtension);
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentNullException.ThrowIfNull(work);

        work.Count(kind: Loads);

        byte[] Read(string stem) {
            var bytecode = File.ReadAllBytes(path: Path.Combine(
                path1: directory,
                path2: $"{stem}.comp{bytecodeExtension}"
            ));

            work.Add(
                amount: bytecode.LongLength,
                kind: BytecodeBytes
            );

            return bytecode;
        }

        return new SdfWorldKernels(
            Ambient: Read(stem: "sdf-world-ambient"),
            Beam: Read(stem: "sdf-beam"),
            BrickBake: Read(stem: "sdf-brick-bake"),
            Composite: Read(stem: "sdf-world-composite"),
            CullArgs: Read(stem: "sdf-cull-args"),
            InstanceCull: Read(stem: "sdf-instance-cull"),
            Primary: Read(stem: "sdf-world-primary"),
            Sky: Read(stem: "sdf-sky"),
            Surface: Read(stem: "sdf-world-surface"),
            Views: Read(stem: "sdf-world-views"),
            ViewsCore: Read(stem: "sdf-world-views-core"),
            ViewsFolds: Read(stem: "sdf-world-views-folds")
        );
    }

    // A nested holder initializes after every kind above, whatever order the members are declared in.
    private static class LoadCounts {
        internal static readonly WorkCounterSet Process = new(
            kinds: [Loads, BytecodeBytes],
            name: LoadWorkSourceName
        );
    }
}
