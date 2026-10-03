using Puck.Abstractions.Counting;
using Puck.Shaders;

namespace Puck.SdfVm;

/// <summary>
/// The compiled SDF kernels of one backend: each <see cref="SdfKernel"/>'s bytecode, SPIR-V for Vulkan or DXIL for
/// Direct3D 12. <see cref="Load(string)"/> reads the set from the deployed assets, one file a kernel named by
/// <see cref="StemOf"/>, and counts every load into <see cref="LoadWork"/>. Each kernel reads the interface
/// <see cref="LayoutOf"/> names, stamped with the instruction set it was compiled against, and
/// <see cref="InterfaceMismatch"/> holds a kernel's reflected bindings to this host's.
/// </summary>
public sealed class SdfKernelSet {
    /// <summary>The name a counters report heads <see cref="LoadWork"/>'s section with.</summary>
    public const string LoadWorkSourceName = "shaders.sdf-kernels";

    private readonly ReadOnlyMemory<byte>[] m_bytecode;

    /// <summary>Initializes a new instance of the <see cref="SdfKernelSet"/> class from each kernel's bytecode.</summary>
    /// <param name="bytecode">The bytecode of every kernel in <see cref="Kernels"/> order; the brick baker's may be empty
    /// for an engine without a brick pool.</param>
    /// <exception cref="ArgumentNullException"><paramref name="bytecode"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="bytecode"/> does not hold one entry per kernel.</exception>
    public SdfKernelSet(IReadOnlyList<ReadOnlyMemory<byte>> bytecode) {
        ArgumentNullException.ThrowIfNull(argument: bytecode);

        if (bytecode.Count != Kernels.Count) {
            throw new ArgumentException(
                message: $"A kernel set holds one bytecode per kernel, {Kernels.Count}; {bytecode.Count} were given.",
                paramName: nameof(bytecode)
            );
        }

        m_bytecode = [.. bytecode];
    }

    /// <summary>Gets every kernel, in pipeline order.</summary>
    public static IReadOnlyList<SdfKernel> Kernels { get; } = Enum.GetValues<SdfKernel>();

    /// <summary>The deployed kernel tree (<c>Assets/Shaders/Sdf</c> next to the application, where the <c>Puck.SdfVm</c>
    /// reference copies the bytecode its build compiles; the bytecode is a build product, never tracked in git). A source
    /// checkout's tree is <c>src/Puck.SdfVm/Assets/Shaders/Sdf</c>, whose build writes the bytecode in place.</summary>
    public static string DeployedTree => Path.Combine(
        path1: AppContext.BaseDirectory,
        path2: "Assets",
        path3: "Shaders",
        path4: "Sdf"
    );
    /// <summary>The standard deploy location of the pass entry points' bytecode, the deployed tree's passes directory
    /// (<see cref="PassesDirectory(string)"/>) — <see cref="Load(string)"/>'s default directory, exposed for callers that
    /// load an individual stage directly (the mesh raster pass's stages, the shared <c>fullscreen.vert</c> vertex stage a
    /// 2D overlay decorator reuses) rather than the whole kernel set.</summary>
    public static string DefaultDirectory => PassesDirectory(tree: DeployedTree);

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

    /// <summary>Gets a kernel's bytecode.</summary>
    /// <param name="kernel">The kernel.</param>
    /// <returns>The bytecode, empty for a brick baker the set does not carry.</returns>
    public ReadOnlyMemory<byte> this[SdfKernel kernel] => m_bytecode[((int)kernel)];

    /// <summary>Returns the file stem a kernel's bytecode ships under, completed by the backend's extension: the one
    /// spelling of each kernel's name, which its pipeline carries too.</summary>
    /// <param name="kernel">The kernel.</param>
    /// <returns>The stem, such as <c>sdf-world-primary</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kernel"/> names no kernel.</exception>
    public static string StemOf(SdfKernel kernel) => kernel switch {
        SdfKernel.IndirectClassify => "sdf-indirect-classify",
        SdfKernel.IndirectTrace => "sdf-indirect-trace",
        SdfKernel.Beam => "sdf-beam",
        SdfKernel.InstanceCull => "sdf-instance-cull",
        SdfKernel.CullArgs => "sdf-cull-args",
        SdfKernel.Primary => "sdf-world-primary",
        SdfKernel.Surface => "sdf-world-surface",
        SdfKernel.Ambient => "sdf-world-ambient",
        SdfKernel.Shadow => "sdf-world-shadow",
        SdfKernel.Views => "sdf-world-views",
        SdfKernel.ViewsCore => "sdf-world-views-core",
        SdfKernel.ViewsFolds => "sdf-world-views-folds",
        SdfKernel.Sky => "sdf-sky-runs",
        SdfKernel.Composite => "sdf-composite",
        SdfKernel.SkyEnvironment => "sdf-sky-environment",
        SdfKernel.SkyEnvironmentReduce => "sdf-sky-environment-reduce",
        SdfKernel.BrickBake => "sdf-brick-bake",
        SdfKernel.Resolve => "sdf-resolve",
        SdfKernel.ShadowFade1 => "sdf-world-shadow-fade1",
        SdfKernel.ShadowFade2 => "sdf-world-shadow-fade2",
        SdfKernel.ViewsFade1 => "sdf-world-views-fade1",
        SdfKernel.ViewsCoreFade1 => "sdf-world-views-core-fade1",
        SdfKernel.ViewsFoldsFade1 => "sdf-world-views-folds-fade1",
        SdfKernel.ViewsFade2 => "sdf-world-views-fade2",
        SdfKernel.ViewsCoreFade2 => "sdf-world-views-core-fade2",
        SdfKernel.ViewsFoldsFade2 => "sdf-world-views-folds-fade2",
        _ => throw new ArgumentOutOfRangeException(paramName: nameof(kernel), actualValue: kernel, message: "Not an SDF kernel."),
    };
    /// <summary>Returns the layout of the interface a kernel reads: the brick baker's
    /// (<see cref="SdfWorldInterfaces.BrickBakeLayout"/>), reconstruction's
    /// (<see cref="SdfWorldInterfaces.ResolveParameters"/>), the sky's and composite's
    /// (<see cref="SdfWorldInterfaces.SkyParameters"/>), the environment map's and its reduction's
    /// (<see cref="SdfWorldInterfaces.EnvironmentParameters"/>), or every native per-view dispatch's
    /// (<see cref="SdfWorldInterfaces.WorldLayout"/>), each stamped with this host's instruction set.</summary>
    /// <param name="kernel">The kernel.</param>
    /// <returns>The layout.</returns>
    public static ShaderInterfaceLayout LayoutOf(SdfKernel kernel) =>
        kernel switch {
            SdfKernel.IndirectClassify or SdfKernel.IndirectTrace => SdfWorldInterfaces.IndirectParameters.Layout,
            SdfKernel.BrickBake => SdfWorldInterfaces.BrickBakeLayout,
            SdfKernel.Resolve => SdfWorldInterfaces.ResolveParameters.Layout,
            SdfKernel.Sky or SdfKernel.Composite => SdfWorldInterfaces.SkyParameters.Layout,
            SdfKernel.ShadowFade1 or SdfKernel.ViewsFade1 or SdfKernel.ViewsCoreFade1 or SdfKernel.ViewsFoldsFade1 => SdfWorldInterfaces.WorldFadeParameters[1].Layout,
            SdfKernel.ShadowFade2 or SdfKernel.ViewsFade2 or SdfKernel.ViewsCoreFade2 or SdfKernel.ViewsFoldsFade2 => SdfWorldInterfaces.WorldFadeParameters[2].Layout,
            SdfKernel.SkyEnvironment or SdfKernel.SkyEnvironmentReduce => SdfWorldInterfaces.EnvironmentParameters.Layout,
            _ => SdfWorldInterfaces.WorldLayout,
        };
    /// <summary>Returns why a kernel's compiled bytecode reads something other than this host's interface, or
    /// <see langword="null"/> when it reads that interface: <see cref="ShaderInterfaceLayout.Mismatch"/> against
    /// <see cref="LayoutOf"/>, which refuses a kernel whose pass block carries another instruction set's stamp or none, and
    /// any binding the layout does not place.</summary>
    /// <param name="kernel">The kernel.</param>
    /// <param name="reflected">The bindings its bytecode declares, as <see cref="ShaderBytecodeReflector"/> reads
    /// them.</param>
    /// <returns>The disagreement, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reflected"/> is <see langword="null"/>.</exception>
    public static string? InterfaceMismatch(SdfKernel kernel, IReadOnlyList<ShaderInterfaceBinding> reflected) =>
        LayoutOf(kernel: kernel).Mismatch(reflected: reflected);
    /// <summary>Returns the directory of a kernel tree that holds its pass entry points and their bytecode: the one
    /// statement of where, under a tree, a kernel set loads from.</summary>
    /// <param name="tree">The kernel tree: <see cref="DeployedTree"/> or a source checkout's.</param>
    /// <returns>The tree's <c>passes</c> directory.</returns>
    public static string PassesDirectory(string tree) => Path.Combine(
        path1: tree,
        path2: "passes"
    );
    /// <summary>Loads the kernel set from the standard deploy location (<see cref="DefaultDirectory"/>).</summary>
    /// <param name="bytecodeExtension">The compiled-kernel extension (<c>".spv"</c> for Vulkan, <c>".dxil"</c> for Direct3D 12).</param>
    /// <returns>The loaded kernel set.</returns>
    public static SdfKernelSet Load(string bytecodeExtension) =>
        Load(
            bytecodeExtension: bytecodeExtension,
            directory: DefaultDirectory
        );
    /// <summary>Loads the kernel set from an explicit directory (for hosts with a non-standard asset layout).</summary>
    /// <param name="bytecodeExtension">The compiled-kernel extension (<c>".spv"</c> for Vulkan, <c>".dxil"</c> for Direct3D 12).</param>
    /// <param name="directory">The directory holding the compiled <c>sdf-*.comp</c> kernels.</param>
    /// <returns>The loaded kernel set.</returns>
    public static SdfKernelSet Load(string bytecodeExtension, string directory) =>
        Load(
            bytecodeExtension: bytecodeExtension,
            directory: directory,
            work: LoadWork
        );
    /// <summary>Loads the kernel set from an explicit directory, counting the load and the bytes it read into
    /// <paramref name="work"/> rather than the process's <see cref="LoadWork"/>.</summary>
    /// <param name="bytecodeExtension">The compiled-kernel extension (<c>".spv"</c> for Vulkan, <c>".dxil"</c> for Direct3D 12).</param>
    /// <param name="directory">The directory holding the compiled <c>sdf-*.comp</c> kernels.</param>
    /// <param name="work">The counts the load adds to; it must count <see cref="Loads"/> and <see cref="BytecodeBytes"/>.</param>
    /// <returns>The loaded kernel set.</returns>
    /// <exception cref="ArgumentException"><paramref name="bytecodeExtension"/> or <paramref name="directory"/> is
    /// empty, or <paramref name="work"/> does not count <see cref="Loads"/> and <see cref="BytecodeBytes"/>.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="work"/> is <see langword="null"/>.</exception>
    /// <exception cref="IOException">A kernel file is missing or cannot be read.</exception>
    public static SdfKernelSet Load(string bytecodeExtension, string directory, WorkCounterSet work) {
        ArgumentException.ThrowIfNullOrEmpty(bytecodeExtension);
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentNullException.ThrowIfNull(work);

        work.Count(kind: Loads);

        var bytecode = new ReadOnlyMemory<byte>[Kernels.Count];

        foreach (var kernel in Kernels) {
            var bytes = File.ReadAllBytes(path: Path.Combine(
                path1: directory,
                path2: $"{StemOf(kernel: kernel)}.comp{bytecodeExtension}"
            ));

            work.Add(
                amount: bytes.LongLength,
                kind: BytecodeBytes
            );
            bytecode[((int)kernel)] = bytes;
        }

        return new SdfKernelSet(bytecode: bytecode);
    }
    /// <summary>Returns the kernel set's content key: a hash over every kernel's bytecode, in <see cref="Kernels"/> order.
    /// A host keys its persistent pipeline cache by it (<see cref="Puck.Abstractions.Gpu.GpuPipelineCacheStore"/>), so a
    /// changed kernel starts a new cache file.</summary>
    /// <returns>A lowercase 16-digit hexadecimal key.</returns>
    public string ContentKey() =>
        Puck.Abstractions.Gpu.GpuPipelineCacheStore.ContentKeyOf(parts: m_bytecode);
    /// <summary>Returns a copy of the set with one kernel's bytecode replaced.</summary>
    /// <param name="kernel">The kernel.</param>
    /// <param name="bytecode">Its new bytecode.</param>
    /// <returns>The new set.</returns>
    public SdfKernelSet With(SdfKernel kernel, ReadOnlyMemory<byte> bytecode) {
        var copy = m_bytecode.ToArray();

        copy[((int)kernel)] = bytecode;

        return new SdfKernelSet(bytecode: copy);
    }
    /// <summary>Returns a copy of the set with each kernel a directory carries replaced, and every other kernel kept:
    /// what a reload reads from a tree that holds only the kernels it changes. A kernel is carried as its source,
    /// <c>{stem}.comp.hlsl</c> named by <see cref="StemOf"/>, which <paramref name="compiler"/> compiles, or as its
    /// bytecode, <c>{stem}.comp</c> completed by <paramref name="bytecodeExtension"/>, which is read as it stands; a
    /// source wins over bytecode beside it. The carried sources compile together, each a cache hit when unchanged.
    /// Counts the load and the bytes it installs into <see cref="LoadWork"/>.</summary>
    /// <param name="bytecodeExtension">The compiled-kernel extension (<c>".spv"</c> for Vulkan, <c>".dxil"</c> for
    /// Direct3D 12), which also selects which of a compiled source's two binaries the set takes.</param>
    /// <param name="directory">The directory holding the kernels it replaces.</param>
    /// <param name="compiler">The compiler a carried source compiles with.</param>
    /// <param name="cancellationToken">The token that cancels the compiles.</param>
    /// <returns>The overlaid set.</returns>
    /// <exception cref="ArgumentException"><paramref name="bytecodeExtension"/> or <paramref name="directory"/> is
    /// empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="compiler"/> is <see langword="null"/>.</exception>
    /// <exception cref="DirectoryNotFoundException"><paramref name="directory"/> does not exist.</exception>
    /// <exception cref="IOException"><paramref name="directory"/> carries no kernel, or a kernel file cannot be
    /// read.</exception>
    /// <exception cref="InvalidDataException">A carried source does not compile; the message names each error's file,
    /// line and column.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public SdfKernelSet Overlaid(string bytecodeExtension, string directory, ShaderCompiler compiler, CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrEmpty(bytecodeExtension);
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentNullException.ThrowIfNull(compiler);

        if (!Directory.Exists(path: directory)) {
            throw new DirectoryNotFoundException(message: $"The kernel directory '{directory}' does not exist.");
        }

        var work = LoadWork;
        var copy = m_bytecode.ToArray();
        var compiles = new List<(SdfKernel Kernel, Task<CompiledShader> Compile)>();
        var carried = 0;

        work.Count(kind: Loads);

        foreach (var kernel in Kernels) {
            var stem = Path.Combine(
                path1: directory,
                path2: $"{StemOf(kernel: kernel)}.comp"
            );
            var source = $"{stem}.hlsl";

            if (File.Exists(path: source)) {
                compiles.Add(item: (kernel, compiler.CompileAsync(
                    cancellationToken: cancellationToken,
                    descriptor: new ShaderCompilationRequest(
                        name: StemOf(kernel: kernel),
                        stages: [new ShaderStageSource(
                            EntryPoint: "CSMain",
                            Path: source,
                            Source: File.ReadAllText(path: source),
                            Stage: ShaderStage.Compute
                        )]
                    )
                )));
                carried++;

                continue;
            }

            var bytecode = $"{stem}{bytecodeExtension}";

            if (!File.Exists(path: bytecode)) {
                continue;
            }

            var bytes = File.ReadAllBytes(path: bytecode);

            work.Add(
                amount: bytes.LongLength,
                kind: BytecodeBytes
            );
            copy[((int)kernel)] = bytes;
            carried++;
        }

        if (carried == 0) {
            throw new IOException(message: $"The kernel directory '{directory}' carries no kernel: no sdf-*.comp.hlsl source and no sdf-*.comp{bytecodeExtension} bytecode.");
        }

        var errors = new List<string>();

        foreach (var (kernel, compile) in compiles) {
            var compiled = compile.GetAwaiter().GetResult();

            if (!compiled.IsSuccess) {
                var reported = compiled.Diagnostics.Where(predicate: static diagnostic => diagnostic.IsError).Select(selector: diagnostic => $"{(diagnostic.Path ?? compiled.SourcePath)}:{diagnostic.Line}:{diagnostic.Column}: {diagnostic.Message}").ToArray();

                errors.AddRange(collection: ((reported.Length != 0)
                    ? reported
                    : [$"{compiled.SourcePath}: the compile produced no bytecode"]));

                continue;
            }

            var bytes = ((bytecodeExtension == ".dxil")
                ? compiled.Dxil
                : compiled.Spirv
            );

            work.Add(
                amount: bytes.Length,
                kind: BytecodeBytes
            );
            copy[((int)kernel)] = bytes;
        }

        if (errors.Count != 0) {
            throw new InvalidDataException(message: $"A kernel source does not compile: {string.Join(separator: "; ", values: errors)}");
        }

        return new SdfKernelSet(bytecode: copy);
    }

    // A nested holder initializes after every kind above, whatever order the members are declared in.
    private static class LoadCounts {
        internal static readonly WorkCounterSet Process = new(
            kinds: [Loads, BytecodeBytes],
            name: LoadWorkSourceName
        );
    }
}
