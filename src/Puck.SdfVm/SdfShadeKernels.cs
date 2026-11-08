using System.Security.Cryptography;
using System.Text;
using Puck.Abstractions;
using Puck.Shaders;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>
/// A canonical shade program set and the kernels that specialize it: the views and glass kernels compiled with
/// <see cref="SdfShadeHlsl.Specialize"/> spliced in as their <c>isa/sdf-shade.hlsli</c> include, so each kernel's include
/// closure is the source its interpreter was compiled from (<see cref="SdfKernelSource"/>) plus the generated programs
/// and nothing else, never a tree's files as they stand when the set compiles. The runtime's specialization and a
/// player build's <c>world prepare</c> plan and compile through this one type, on the shared compile turns
/// (<see cref="ShaderCompileTurns"/>) and through the compiler's own cache, which keys each output by its closure, so
/// a second boot of an unchanged world runs no tool.
/// </summary>
public sealed class SdfShadeKernels {
    /// <summary>The kernels a set specializes, each with its plain counterpart that shades no program: the unsplit views
    /// and glass kernels, and the binned views kernel that shades a binned view's tiles with programs.</summary>
    public static IReadOnlyList<SdfKernel> Specialized { get; } = [SdfKernel.Views, SdfKernel.Glass, SdfKernel.ViewsShade];

    private readonly SdfShadeProgram[] m_programs;
    private readonly HashSet<string> m_identities;
    private readonly SdfKernelSource?[] m_sources;

    /// <summary>Initializes a new instance of the <see cref="SdfShadeKernels"/> class.</summary>
    /// <param name="programs">The admitted programs; duplicates by identity are ignored.</param>
    /// <param name="kernels">The running kernel set whose interpreter the specialization replaces: each specialized kernel
    /// compiles the source its interpreter was compiled from (<see cref="SdfKernelSet.SourceOf"/>), and the set's identity
    /// carries the kernels' (<see cref="InterpreterOf"/>), so a reload that changes them makes another request.</param>
    /// <exception cref="ArgumentNullException"><paramref name="programs"/> or <paramref name="kernels"/> is
    /// <see langword="null"/>.</exception>
    public SdfShadeKernels(IEnumerable<SdfShadeProgram> programs, SdfKernelSet kernels) {
        ArgumentNullException.ThrowIfNull(programs);
        ArgumentNullException.ThrowIfNull(kernels);
        m_programs = SdfShadeHlsl.Canonical(programs: programs);
        m_identities = new HashSet<string>(collection: m_programs.Select(selector: static p => p.Identity), comparer: StringComparer.Ordinal);
        m_sources = [.. Specialized.Select(selector: kernels.SourceOf)];
        Interpreter = InterpreterOf(kernels: kernels);
        Identity = Convert.ToHexStringLower(inArray: SHA256.HashData(source: Encoding.UTF8.GetBytes(
            s: $"{Interpreter}\n{SdfShadeOps.Fingerprint}\n{string.Join(separator: '\n', values: m_identities.Order(comparer: StringComparer.Ordinal))}")));
    }

    /// <summary>Gets the identity a publication must still match: the program identities, the op semantics and the
    /// running kernels' identity (<see cref="Interpreter"/>). Parameter values and author names do not enter it.</summary>
    public string Identity { get; }
    /// <summary>Gets the identity of the kernels whose interpreter this set replaces (<see cref="InterpreterOf"/>).</summary>
    public string Interpreter { get; }
    /// <summary>Gets the canonical programs, ordered by identity.</summary>
    public IReadOnlyList<SdfShadeProgram> Programs => m_programs;

    /// <summary>Returns whether the set holds a program of the given identity, so its kernels shade it.</summary>
    /// <param name="identity">The program identity (<see cref="SdfShadeProgram.Identity"/>).</param>
    /// <returns><see langword="true"/> when the set holds it.</returns>
    public bool Holds(string identity) => m_identities.Contains(item: identity);
    /// <summary>Returns whether the set holds every program a frame may invoke, the one condition under which its
    /// kernels may record that frame: a program outside the set would execute nothing.</summary>
    /// <param name="active">The identities of the frame's active programs.</param>
    /// <returns><see langword="true"/> when the set holds every one.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="active"/> is <see langword="null"/>.</exception>
    public bool Covers(IReadOnlySet<string> active) {
        ArgumentNullException.ThrowIfNull(active);

        return m_identities.IsSupersetOf(other: active);
    }

    /// <summary>Returns the identity of a kernel set as a specialization replaces it: its bytecode's content key
    /// (<see cref="SdfKernelSet.ContentKey"/>) and the content key of the source each specialized kernel was compiled
    /// from (<see cref="SdfKernelSource.Key"/>), or that none is known. A set published under one identity specializes
    /// exactly the sources it names.</summary>
    /// <param name="kernels">The kernel set.</param>
    /// <returns>The identity, 64 lowercase hexadecimal digits.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="kernels"/> is <see langword="null"/>.</exception>
    public static string InterpreterOf(SdfKernelSet kernels) {
        ArgumentNullException.ThrowIfNull(kernels);

        return Convert.ToHexStringLower(inArray: SHA256.HashData(source: Encoding.UTF8.GetBytes(
            s: $"{kernels.ContentKey()}\n{string.Join(separator: '\n', values: Specialized.Select(selector: kernel => (kernels.SourceOf(kernel: kernel)?.Key ?? "unknown")))}")));
    }
    /// <summary>Reads the source of each specialized kernel from a kernel tree, for a build that compiles the tree's
    /// kernels itself (<c>world prepare</c>) rather than replacing a running set's.</summary>
    /// <param name="tree">The kernel tree (<see cref="SdfKernelSet.DeployedTree"/>, or a source checkout's).</param>
    /// <returns>One source a kernel, in <see cref="Specialized"/> order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tree"/> is <see langword="null"/>.</exception>
    /// <exception cref="IOException">A kernel source cannot be read.</exception>
    /// <exception cref="ShaderClosureRefusedException">A kernel's closure is refused.</exception>
    public static IReadOnlyList<SdfKernelSource> SourcesOf(string tree) {
        ArgumentNullException.ThrowIfNull(tree);

        return [.. Specialized.Select(selector: kernel => SdfKernelSource.Read(directory: SdfKernelSet.PassesDirectory(tree: tree), kernel: kernel))];
    }
    /// <summary>Plans each specialized kernel of a program set: the kernel's source as it was taken, with the generated
    /// programs in place of its <c>isa/sdf-shade.hlsli</c>. Nothing runs and no file is read: every include comes from
    /// the source, and one the source does not hold refuses the plan.</summary>
    /// <param name="programs">The admitted programs.</param>
    /// <param name="compiler">The compiler whose cache names the outputs.</param>
    /// <param name="target">The target the kernels compile to.</param>
    /// <param name="sources">The source of each kernel in <see cref="Specialized"/> order: the running interpreter's
    /// (<see cref="SdfKernelSet.SourceOf"/>), or a tree's (<see cref="SourcesOf"/>).</param>
    /// <returns>One plan a kernel, in <see cref="Specialized"/> order.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="sources"/> does not hold one source per specialized
    /// kernel.</exception>
    /// <exception cref="ShaderClosureRefusedException">A kernel's closure reaches an include its source does not
    /// hold.</exception>
    /// <exception cref="InvalidOperationException">A kernel's closure does not reach the generated include.</exception>
    public static IReadOnlyList<(SdfKernel Kernel, ShaderOutputPlan Plan)> Plan(IEnumerable<SdfShadeProgram> programs, ShaderCompiler compiler, ShaderTarget target, IReadOnlyList<SdfKernelSource> sources) {
        ArgumentNullException.ThrowIfNull(programs);
        ArgumentNullException.ThrowIfNull(compiler);
        ArgumentNullException.ThrowIfNull(sources);

        if (sources.Count != Specialized.Count) {
            throw new ArgumentException(message: $"A specialization plans {Specialized.Count} kernels; {sources.Count} sources were given.", paramName: nameof(sources));
        }

        var text = SdfShadeHlsl.Specialize(programs: programs);
        var hash = ShaderSourceClosure.HashOf(text: text);

        return [.. Specialized.Select(selector: (kernel, index) => {
            var source = sources[index];
            // A kernel's entry point lies in its tree's passes directory, beside the tree's isa directory; the closure
            // resolves each include to Path.GetFullPath's spelling and looks generated text up by it.
            var include = Path.GetFullPath(path: Path.Combine(path1: Path.GetDirectoryName(path: source.Stage.Path)!, path2: "..", path3: "isa", path4: SdfShadeHlsl.FileName));
            var plan = compiler.Plan(
                generated: new Dictionary<string, string>(comparer: PuckPaths.Comparer) { [include] = text },
                readInclude: source.Include,
                stage: source.Stage,
                target: target
            );

            // The one place a specialization's closure is checked to hold its programs: a kernel that never reached the
            // generated include would compile the interpreter under the specialization's name.
            if (!plan.Includes.Any(predicate: dependency => PuckPaths.Comparer.Equals(x: dependency.Path, y: include) && (dependency.ContentHash == hash))) {
                throw new InvalidOperationException(message: $"The specialized '{SdfKernelSet.StemOf(kernel: kernel)}' does not include the generated programs at '{include}'.");
            }
            return (kernel, plan);
        })];
    }
    /// <summary>Compiles this set's kernels off the calling thread on the shared compile turns, the shorter recorded
    /// compile first, each from the source its interpreter was compiled from. A cache hit runs no tool; every request and
    /// tool run is counted in the compiler's work.</summary>
    /// <param name="compiler">The compiler.</param>
    /// <param name="target">The target the kernels compile to.</param>
    /// <param name="cancellationToken">The token that cancels the compiles.</param>
    /// <returns>Each kernel's bytecode, in <see cref="Specialized"/> order.</returns>
    /// <exception cref="InvalidOperationException">A running kernel's source is unknown (its bytecode arrived without
    /// one), so no specialization can describe it; or a closure does not reach the generated include.</exception>
    /// <exception cref="ShaderClosureRefusedException">A kernel's closure is refused.</exception>
    /// <exception cref="InvalidDataException">A kernel does not compile; the message names each error.</exception>
    public Task<(SdfKernel Kernel, ReadOnlyMemory<byte> Bytecode)[]> CompileAsync(ShaderCompiler compiler, ShaderTarget target, CancellationToken cancellationToken = default) {
        if (Array.IndexOf(array: m_sources, value: null) is var missing and >= 0) {
            throw new InvalidOperationException(message: $"The running '{SdfKernelSet.StemOf(kernel: Specialized[missing])}' was installed without its source, so no specialization can describe it; it stays interpreted.");
        }

        return CompileAsync(compiler: compiler, plans: Plan(compiler: compiler, programs: m_programs, sources: [.. m_sources.Select(selector: static source => source!)], target: target), cancellationToken: cancellationToken);
    }
    /// <summary>Compiles planned kernels off the calling thread on the shared compile turns, the shorter recorded compile
    /// first.</summary>
    /// <param name="compiler">The compiler the plans were made with.</param>
    /// <param name="plans">The plans (<see cref="Plan"/>).</param>
    /// <param name="cancellationToken">The token that cancels the compiles.</param>
    /// <returns>Each kernel's bytecode, in the plans' order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="compiler"/> or <paramref name="plans"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">A kernel does not compile; the message names each error.</exception>
    public static async Task<(SdfKernel Kernel, ReadOnlyMemory<byte> Bytecode)[]> CompileAsync(ShaderCompiler compiler, IReadOnlyList<(SdfKernel Kernel, ShaderOutputPlan Plan)> plans, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(compiler);
        ArgumentNullException.ThrowIfNull(plans);

        var results = new (SdfKernel Kernel, ReadOnlyMemory<byte> Bytecode)[plans.Count];
        var order = Enumerable.Range(start: 0, count: plans.Count).OrderBy(keySelector: index => (plans[index].Plan.RecordedDuration ?? TimeSpan.MaxValue));

        await Task.WhenAll(tasks: order.Select(selector: async index => {
            var (kernel, plan) = plans[index];
            var result = await ShaderCompileTurns.CompileAsync(cancellationToken: cancellationToken, compiler: compiler, plan: plan).ConfigureAwait(continueOnCapturedContext: false);

            if (!result.IsSuccess) {
                var errors = result.Diagnostics.Where(predicate: static d => d.IsError).Select(selector: d => $"{(d.Path ?? plan.Stage.Path)}:{d.Line}:{d.Column}: {d.Message}").ToArray();

                throw new InvalidDataException(message: $"The specialized '{SdfKernelSet.StemOf(kernel: kernel)}' does not compile: {((errors.Length != 0) ? string.Join(separator: "; ", values: errors) : "the compile produced no bytecode")}");
            }
            results[index] = (kernel, result.Bytecode!);
        })).ConfigureAwait(continueOnCapturedContext: false);

        return results;
    }
}
