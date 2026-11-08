using System.Security.Cryptography;
using System.Text;
using Puck.Shaders;

namespace Puck.SdfVm;

/// <summary>
/// The source one kernel's bytecode was compiled from: its stage source and the text of every include its closure
/// reached, read once, when the bytecode was taken, and never read from disk again. A kernel set carries it beside the
/// bytecode it describes (<see cref="SdfKernelSet.SourceOf"/>), and a specialization compiles exactly this text with its
/// generated programs in place of one include (<see cref="SdfShadeKernels.Plan"/>), so a kernel tree edited since cannot
/// enter a set published under the running kernels' identity.
/// </summary>
public sealed class SdfKernelSource {
    private readonly IReadOnlyDictionary<string, string> m_includes;

    private SdfKernelSource(ShaderStageSource stage, IReadOnlyDictionary<string, string> includes) {
        Stage = stage;
        m_includes = includes;

        var key = new StringBuilder().Append(value: Path.GetFullPath(path: stage.Path)).Append(value: '|').Append(value: ShaderSourceClosure.HashOf(text: stage.Source)).Append(value: '\n');

        foreach (var (path, text) in includes.OrderBy(keySelector: static include => include.Key, comparer: StringComparer.Ordinal)) {
            key.Append(value: path).Append(value: '|').Append(value: ShaderSourceClosure.HashOf(text: text)).Append(value: '\n');
        }

        Key = Convert.ToHexStringLower(inArray: SHA256.HashData(source: Encoding.UTF8.GetBytes(s: key.ToString())));
    }

    /// <summary>Gets the content key of the source: every file's full path and content hash, the stage's first.</summary>
    public string Key { get; }
    /// <summary>Gets the kernel's stage source, its path and the text compiled for it.</summary>
    public ShaderStageSource Stage { get; }

    /// <summary>Returns the text an include held when the source was taken.</summary>
    /// <param name="path">The include's full path.</param>
    /// <returns>The text, or <see langword="null"/> when the kernel's closure does not reach the path.</returns>
    public string? Include(string path) => (m_includes.TryGetValue(key: path, value: out var text) ? text : null);

    /// <summary>Takes the source a compile planned: the stage and the closure the plan read.</summary>
    /// <param name="plan">The plan of the compile whose bytecode the source describes.</param>
    /// <returns>The source.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is <see langword="null"/>.</exception>
    public static SdfKernelSource Of(ShaderOutputPlan plan) {
        ArgumentNullException.ThrowIfNull(plan);

        return new SdfKernelSource(includes: plan.Contents, stage: plan.Stage);
    }
    /// <summary>Reads a kernel's source and its include closure from a kernel tree's passes directory.</summary>
    /// <param name="directory">The passes directory (<see cref="SdfKernelSet.PassesDirectory"/>).</param>
    /// <param name="kernel">The kernel, whose entry point is <c>{stem}.comp.hlsl</c> (<see cref="SdfKernelSet.StemOf"/>).</param>
    /// <returns>The source.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="directory"/> is <see langword="null"/>.</exception>
    /// <exception cref="IOException">The entry point cannot be read.</exception>
    /// <exception cref="ShaderClosureRefusedException">The entry point's closure is refused: an include is missing or a
    /// limit is exceeded.</exception>
    public static SdfKernelSource Read(string directory, SdfKernel kernel) {
        ArgumentNullException.ThrowIfNull(directory);

        var path = Path.GetFullPath(path: Path.Combine(path1: directory, path2: $"{SdfKernelSet.StemOf(kernel: kernel)}.comp.hlsl"));
        var stage = new ShaderStageSource(EntryPoint: "CSMain", Path: path, Source: File.ReadAllText(path: path), Stage: ShaderStage.Compute);
        var closure = ShaderSourceClosure.Collect(limits: ShaderSourceLimits.Default, sources: [(stage.Path, stage.Source)]);

        return new SdfKernelSource(includes: closure.Contents, stage: stage);
    }
}
