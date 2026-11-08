using System.Security.Cryptography;
using System.Text;
using Puck.Abstractions;
using Puck.Shaders;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>
/// A canonical shade program set and the kernels that specialize it: the views and glass kernels compiled with
/// <see cref="SdfShadeHlsl.Specialize"/> spliced in as their <c>isa/sdf-shade.hlsli</c> include, so each kernel's include
/// closure is its tree's sources plus the generated programs and nothing else. The runtime's specialization and a
/// player build's <c>world prepare</c> plan and compile through this one type, on the shared compile turns
/// (<see cref="ShaderCompileTurns"/>) and through the compiler's own cache, which keys each output by its closure, so
/// a second boot of an unchanged world runs no tool.
/// </summary>
public sealed class SdfShadeKernels {
    /// <summary>The kernels a set specializes, each with its plain counterpart a frame with no program renders with.</summary>
    public static IReadOnlyList<SdfKernel> Specialized { get; } = [SdfKernel.Views, SdfKernel.Glass];

    private readonly SdfShadeProgram[] m_programs;
    private readonly HashSet<string> m_identities;

    /// <summary>Initializes a new instance of the <see cref="SdfShadeKernels"/> class.</summary>
    /// <param name="programs">The admitted programs; duplicates by identity are ignored.</param>
    /// <param name="interpreter">The content key of the kernel set whose interpreter the specialization replaces
    /// (<see cref="SdfKernelSet.ContentKey"/>): a reload that changes the running kernels makes another request, so a
    /// specialization compiled from the earlier sources never publishes over them.</param>
    /// <exception cref="ArgumentNullException"><paramref name="programs"/> or <paramref name="interpreter"/> is
    /// <see langword="null"/>.</exception>
    public SdfShadeKernels(IEnumerable<SdfShadeProgram> programs, string interpreter) {
        ArgumentNullException.ThrowIfNull(programs);
        ArgumentNullException.ThrowIfNull(interpreter);
        m_programs = SdfShadeHlsl.Canonical(programs: programs);
        m_identities = new HashSet<string>(collection: m_programs.Select(selector: static p => p.Identity), comparer: StringComparer.Ordinal);
        Interpreter = interpreter;
        Identity = Convert.ToHexStringLower(inArray: SHA256.HashData(source: Encoding.UTF8.GetBytes(
            s: $"{interpreter}\n{SdfShadeOps.Fingerprint}\n{string.Join(separator: '\n', values: m_identities.Order(comparer: StringComparer.Ordinal))}")));
    }

    /// <summary>Gets the identity a publication must still match: the program identities, the op semantics and the
    /// interpreter's content key. Parameter values and author names do not enter it.</summary>
    public string Identity { get; }
    /// <summary>Gets the content key of the kernel set whose interpreter this set replaces.</summary>
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

    /// <summary>Plans each specialized kernel of a program set: the tree's kernel source with the generated programs in
    /// place of its <c>isa/sdf-shade.hlsli</c>. Nothing runs.</summary>
    /// <param name="programs">The admitted programs.</param>
    /// <param name="compiler">The compiler whose cache names the outputs.</param>
    /// <param name="target">The target the kernels compile to.</param>
    /// <param name="tree">The kernel tree whose sources the running interpreter was compiled from: the deployed tree
    /// (<see cref="SdfKernelSet.DeployedTree"/>), or the source tree the latest reload installed.</param>
    /// <returns>One plan a kernel, in <see cref="Specialized"/> order.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="IOException">A kernel source cannot be read.</exception>
    /// <exception cref="ShaderClosureRefusedException">A kernel's closure is refused.</exception>
    public static IReadOnlyList<(SdfKernel Kernel, ShaderOutputPlan Plan)> Plan(IEnumerable<SdfShadeProgram> programs, ShaderCompiler compiler, ShaderTarget target, string tree) {
        ArgumentNullException.ThrowIfNull(programs);
        ArgumentNullException.ThrowIfNull(compiler);
        ArgumentNullException.ThrowIfNull(tree);

        // The closure resolves each include to Path.GetFullPath's spelling and looks generated text up by it.
        var include = Path.GetFullPath(path: Path.Combine(path1: tree, path2: "isa", path3: SdfShadeHlsl.FileName));
        var text = SdfShadeHlsl.Specialize(programs: programs);
        var hash = ShaderSourceClosure.HashOf(text: text);
        var generated = new Dictionary<string, string>(comparer: PuckPaths.Comparer) { [include] = text };

        return [.. Specialized.Select(selector: kernel => {
            var path = Path.GetFullPath(path: Path.Combine(path1: SdfKernelSet.PassesDirectory(tree: tree), path2: $"{SdfKernelSet.StemOf(kernel: kernel)}.comp.hlsl"));
            var plan = compiler.Plan(
                generated: generated,
                stage: new ShaderStageSource(EntryPoint: "CSMain", Path: path, Source: File.ReadAllText(path: path), Stage: ShaderStage.Compute),
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
    /// compile first. A cache hit runs no tool; every request and tool run is counted in the compiler's work.</summary>
    /// <param name="compiler">The compiler.</param>
    /// <param name="target">The target the kernels compile to.</param>
    /// <param name="tree">The kernel tree (<see cref="Plan"/>).</param>
    /// <param name="cancellationToken">The token that cancels the compiles.</param>
    /// <returns>Each kernel's bytecode, in <see cref="Specialized"/> order.</returns>
    /// <exception cref="InvalidDataException">A kernel does not compile; the message names each error.</exception>
    public Task<(SdfKernel Kernel, ReadOnlyMemory<byte> Bytecode)[]> CompileAsync(ShaderCompiler compiler, ShaderTarget target, string tree, CancellationToken cancellationToken = default) =>
        CompileAsync(compiler: compiler, plans: Plan(compiler: compiler, programs: m_programs, target: target, tree: tree), cancellationToken: cancellationToken);
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
