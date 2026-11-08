using System.Text;
using System.Security.Cryptography;
using Puck.Abstractions;
using Puck.Shaders;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>An immutable request over admitted identities and the current interpreter. Runtime loading and player
/// preparation use the same source splice, compiler cache and shared compile turns.</summary>
public sealed class SdfShadeKernels {
    private readonly SdfShadeProgram[] m_programs;
    private readonly string m_interpreter;
    private readonly bool m_fallback;

    /// <summary>Captures a program set. Parameters and author names do not enter its identity.</summary>
    public SdfShadeKernels(IEnumerable<SdfShadeProgram> programs, SdfKernelSet kernels, bool fallback = true) {
        m_programs = programs.DistinctBy(p => p.Identity).OrderBy(p => p.Identity, StringComparer.Ordinal).ToArray();
        m_interpreter = kernels.ContentKey();
        m_fallback = fallback;
        Identity = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            m_interpreter + "\n" + SdfShadeOps.Fingerprint + "\n" + fallback + "\n" + string.Join("\n", m_programs.Select(p => p.Identity)))));
    }

    /// <summary>The exact set, interpreter and semantics identity a publication must still match.</summary>
    public string Identity { get; }
    /// <summary>The canonical programs represented by this request.</summary>
    public IReadOnlyList<SdfShadeProgram> Programs => m_programs;

    /// <summary>Plans the views and glass kernels with the same generated phase include.</summary>
    public IReadOnlyList<(SdfKernel Kernel, ShaderOutputPlan Plan)> Plan(ShaderCompiler compiler, ShaderTarget target, string tree) {
        var include = PuckPaths.Normalize(Path.Combine(tree, "isa", SdfShadeHlsl.FileName));
        var generated = new Dictionary<string, string>(PuckPaths.Comparer) {
            [include] = SdfShadeHlsl.Specialize(m_programs, m_fallback),
        };
        return new[] { SdfKernel.Views, SdfKernel.Glass }.Select(kernel => {
            var path = PuckPaths.Normalize(Path.Combine(SdfKernelSet.PassesDirectory(tree), SdfKernelSet.StemOf(kernel) + ".comp.hlsl"));
            var source = "// Interpreter content: " + m_interpreter + "\n" + File.ReadAllText(path);
            return (kernel, compiler.Plan(new ShaderStageSource(ShaderStage.Compute, path, source, "CSMain"), target, generated: generated));
        }).ToArray();
    }

    /// <summary>Compiles this request off the frame thread, shortest recorded output first. Cache hits run no tool.</summary>
    public async Task<IReadOnlyList<(SdfKernel Kernel, ReadOnlyMemory<byte> Bytecode)>> CompileAsync(
        ShaderCompiler compiler, ShaderTarget target, string tree, CancellationToken cancellationToken = default) {
        var plans = Plan(compiler, target, tree);
        var tasks = plans.OrderBy(p => p.Plan.RecordedDuration ?? TimeSpan.MaxValue).Select(async p => {
            var result = await ShaderCompileTurns.CompileAsync(compiler, p.Plan, cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess) {
                throw new InvalidDataException(string.Join("\n", result.Diagnostics.Select(d => $"{d.Path}:{d.Line}:{d.Column}: {d.Message}")));
            }
            return (p.Kernel, (ReadOnlyMemory<byte>)result.Bytecode!);
        }).ToArray();
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }
}
