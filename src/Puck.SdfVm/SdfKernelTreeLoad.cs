using System.Collections.Concurrent;
using Puck.Hosting;
using Puck.Shaders;

namespace Puck.SdfVm;

/// <summary>
/// A reload's read of a kernel tree, delivered kernel by kernel: each kernel the tree carries as bytecode
/// (<c>{stem}.comp</c> plus the backend's extension) is ready at once, and each it carries as source
/// (<c>{stem}.comp.hlsl</c>, named by <see cref="SdfKernelSet.StemOf"/>, which wins over bytecode beside it) compiles to
/// the running backend's target alone. Compiles run at most one per processor, shortest recorded compile first, so the
/// kernels that compile quickly are ready while the heavy ones still compile, and a reload installs each batch as it
/// arrives (<see cref="TakeReady"/>). An unchanged source is a cache hit in the compiler's cache.
/// </summary>
public sealed class SdfKernelTreeLoad : IDisposable {
    // The finished compile's result: the build's value is only that it finished.
    private static readonly object Finished = new();

    private readonly BackgroundBuild<object> m_build = new();
    private readonly ConcurrentQueue<(SdfKernel Kernel, ReadOnlyMemory<byte> Bytecode, SdfKernelSource? Source)> m_ready = new();
    private readonly ConcurrentQueue<string> m_errors = new();
    private string? m_failure;
    private bool m_settled;

    private SdfKernelTreeLoad(ShaderTarget target, string directory, ShaderCompiler compiler) {
        var bytecodeExtension = target.FileExtension();
        var sources = new List<(SdfKernel Kernel, string Path)>();

        foreach (var kernel in SdfKernelSet.Kernels) {
            var stem = Path.Combine(
                path1: directory,
                path2: $"{SdfKernelSet.StemOf(kernel: kernel)}.comp"
            );
            var source = $"{stem}.hlsl";

            if (File.Exists(path: source)) {
                sources.Add(item: (kernel, source));

                continue;
            }

            var bytecode = $"{stem}{bytecodeExtension}";

            if (!File.Exists(path: bytecode)) {
                continue;
            }

            var bytes = File.ReadAllBytes(path: bytecode);

            SdfKernelSet.LoadWork.Add(
                amount: bytes.LongLength,
                kind: SdfKernelSet.BytecodeBytes
            );
            m_ready.Enqueue(item: (kernel, bytes, null));
        }

        if ((sources.Count == 0) && m_ready.IsEmpty) {
            throw new IOException(message: $"The kernel directory '{directory}' carries no kernel: no sdf-*.comp.hlsl source and no sdf-*.comp{bytecodeExtension} bytecode.");
        }

        SdfKernelSet.LoadWork.Count(kind: SdfKernelSet.Loads);
        m_build.Start(build: new Func<CancellationToken, Task<object>>(async cancellationToken => {
            await CompileAsync(
                cancellationToken: cancellationToken,
                compiler: compiler,
                sources: sources,
                target: target
            ).ConfigureAwait(continueOnCapturedContext: false);

            return Finished;
        }));
    }

    /// <summary>Gets whether every carried kernel has been read or compiled, successfully or not; <see langword="false"/>
    /// once the load is disposed while its compiles still ran.</summary>
    public bool IsCompleted => Settle();

    /// <summary>Starts reading a kernel tree's passes directory.</summary>
    /// <param name="target">The running backend's target: its compiled-kernel extension names the bytecode a carried kernel
    /// is read from, and a carried source compiles to it alone.</param>
    /// <param name="directory">The directory holding the kernels it replaces.</param>
    /// <param name="compiler">The compiler a carried source compiles with.</param>
    /// <returns>The load, whose kernels arrive through <see cref="TakeReady"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="directory"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="compiler"/> is <see langword="null"/>.</exception>
    /// <exception cref="DirectoryNotFoundException"><paramref name="directory"/> does not exist.</exception>
    /// <exception cref="IOException"><paramref name="directory"/> carries no kernel, or a kernel file cannot be
    /// read.</exception>
    public static SdfKernelTreeLoad Start(ShaderTarget target, string directory, ShaderCompiler compiler) {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentNullException.ThrowIfNull(compiler);

        if (!Directory.Exists(path: directory)) {
            throw new DirectoryNotFoundException(message: $"The kernel directory '{directory}' does not exist.");
        }

        return new SdfKernelTreeLoad(
            compiler: compiler,
            directory: directory,
            target: target
        );
    }

    /// <summary>Removes and returns every kernel read or compiled since the last call.</summary>
    /// <returns>The ready kernels, each with its bytecode and the source it compiled (<see langword="null"/> for one the
    /// tree carried as bytecode), possibly none.</returns>
    public IReadOnlyList<(SdfKernel Kernel, ReadOnlyMemory<byte> Bytecode, SdfKernelSource? Source)> TakeReady() {
        if (m_ready.IsEmpty) {
            return [];
        }

        var ready = new List<(SdfKernel, ReadOnlyMemory<byte>, SdfKernelSource?)>();

        while (m_ready.TryDequeue(result: out var item)) {
            ready.Add(item: item);
        }

        return ready;
    }
    /// <summary>Returns why the load failed once it has completed: each compile error's file, line and column, or a
    /// fault of the load itself; <see langword="null"/> while it runs or when every kernel compiled.</summary>
    /// <returns>The failure, or <see langword="null"/>.</returns>
    public string? FailureOf() => (Settle() ? m_failure : null);
    /// <summary>Cancels the compiles still running without waiting for them to stop. A second call does nothing.</summary>
    public void Dispose() =>
        // The compiles hold no device object, so nothing waits for them: the build releases its token source once they stop.
        m_build.Cancel();
    /// <summary>Blocks until every carried kernel has been read or compiled, successfully or not; a failed load is
    /// reported by <see cref="FailureOf"/>.</summary>
    /// <param name="cancellationToken">The token that ends the wait; the compiles keep running.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public void WaitFinished(CancellationToken cancellationToken) =>
        m_build.WaitFinished(cancellationToken: cancellationToken);

    // Takes the finished build once and keeps what it reports, since the build forgets it after a take.
    private bool Settle() {
        if (m_settled) {
            return true;
        }
        if (!m_build.TryTake(error: out var fault, result: out _)) {
            return false;
        }

        m_settled = true;
        m_failure = ((fault is not null)
            ? ((fault is OperationCanceledException) ? "canceled" : fault.Message)
            : (m_errors.IsEmpty
                ? null
                : $"A kernel source does not compile: {string.Join(separator: "; ", values: m_errors)}"
            )
        );

        return true;
    }

    private async Task CompileAsync(IReadOnlyList<(SdfKernel Kernel, string Path)> sources, ShaderTarget target, ShaderCompiler compiler, CancellationToken cancellationToken) {
        var plans = new List<(SdfKernel Kernel, ShaderOutputPlan Plan)>(capacity: sources.Count);

        foreach (var (kernel, path) in sources) {
            try {
                plans.Add(item: (kernel, compiler.Plan(
                    stage: new ShaderStageSource(
                        EntryPoint: "CSMain",
                        IncludeDirectories: ShaderIncludeDirectories.Of(sourcePath: path),
                        Path: path,
                        Source: await File.ReadAllTextAsync(path: path, cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false),
                        Stage: ShaderStage.Compute
                    ),
                    target: target
                )));
            } catch (Exception exception) when ((exception is ShaderClosureRefusedException or IOException or UnauthorizedAccessException)) {
                m_errors.Enqueue(item: $"{path}: {exception.Message}");
            }
        }

        // Shortest first: the kernels a compile delivers quickly install while the heavy ones still run. A plan with no
        // recorded duration (a kernel this cache has never compiled) goes last, as the slowest.
        var compiles = plans.OrderBy(keySelector: static plan => (plan.Plan.RecordedDuration ?? TimeSpan.MaxValue)).Select(selector: async plan => {
                var result = await ShaderCompileTurns.CompileAsync(
                    compiler: compiler,
                    cancellationToken: cancellationToken,
                    plan: plan.Plan
                ).ConfigureAwait(continueOnCapturedContext: false);

                if (!result.IsSuccess) {
                    var reported = result.Diagnostics.Where(predicate: static diagnostic => diagnostic.IsError).Select(selector: diagnostic => $"{(diagnostic.Path ?? plan.Plan.Stage.Path)}:{diagnostic.Line}:{diagnostic.Column}: {diagnostic.Message}").ToArray();

                    foreach (var error in ((reported.Length != 0) ? reported : [$"{plan.Plan.Stage.Path}: the compile produced no bytecode"])) {
                        m_errors.Enqueue(item: error);
                    }

                    return;
                }

                SdfKernelSet.LoadWork.Add(
                    amount: result.Bytecode!.LongLength,
                    kind: SdfKernelSet.BytecodeBytes
                );
                m_ready.Enqueue(item: (plan.Kernel, result.Bytecode, SdfKernelSource.Of(plan: plan.Plan)));
        }).ToArray();

        await Task.WhenAll(tasks: compiles).ConfigureAwait(continueOnCapturedContext: false);
    }
}
