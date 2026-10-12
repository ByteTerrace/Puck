using System.Collections.Concurrent;
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
    private readonly CancellationTokenSource m_cancellation;
    private readonly ConcurrentQueue<(SdfKernel Kernel, ReadOnlyMemory<byte> Bytecode)> m_ready = new();
    private readonly ConcurrentQueue<string> m_errors = new();
    private readonly Task m_completion;

    private SdfKernelTreeLoad(string bytecodeExtension, string directory, ShaderCompiler compiler, CancellationToken cancellationToken) {
        m_cancellation = CancellationTokenSource.CreateLinkedTokenSource(token: cancellationToken);

        var target = ((bytecodeExtension == ".dxil")
            ? ShaderTarget.Dxil
            : ShaderTarget.Spirv
        );
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
            m_ready.Enqueue(item: (kernel, bytes));
        }

        if ((sources.Count == 0) && m_ready.IsEmpty) {
            throw new IOException(message: $"The kernel directory '{directory}' carries no kernel: no sdf-*.comp.hlsl source and no sdf-*.comp{bytecodeExtension} bytecode.");
        }

        SdfKernelSet.LoadWork.Count(kind: SdfKernelSet.Loads);
        m_completion = Task.Run(
            cancellationToken: m_cancellation.Token,
            function: () => CompileAsync(
                cancellationToken: m_cancellation.Token,
                compiler: compiler,
                sources: sources,
                target: target
            )
        );
    }

    /// <summary>Gets whether every carried kernel has been read or compiled, successfully or not.</summary>
    public bool IsCompleted => m_completion.IsCompleted;
    /// <summary>Gets the task that completes once every carried kernel has been read or compiled.</summary>
    public Task Completion => m_completion;

    /// <summary>Starts reading a kernel tree's passes directory.</summary>
    /// <param name="bytecodeExtension">The running backend's compiled-kernel extension (<c>".spv"</c> for Vulkan,
    /// <c>".dxil"</c> for Direct3D 12), which also selects the one target a carried source compiles to.</param>
    /// <param name="directory">The directory holding the kernels it replaces.</param>
    /// <param name="compiler">The compiler a carried source compiles with.</param>
    /// <param name="cancellationToken">The token that cancels the compiles.</param>
    /// <returns>The load, whose kernels arrive through <see cref="TakeReady"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="bytecodeExtension"/> or <paramref name="directory"/> is
    /// empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="compiler"/> is <see langword="null"/>.</exception>
    /// <exception cref="DirectoryNotFoundException"><paramref name="directory"/> does not exist.</exception>
    /// <exception cref="IOException"><paramref name="directory"/> carries no kernel, or a kernel file cannot be
    /// read.</exception>
    public static SdfKernelTreeLoad Start(string bytecodeExtension, string directory, ShaderCompiler compiler, CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrEmpty(bytecodeExtension);
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentNullException.ThrowIfNull(compiler);

        if (!Directory.Exists(path: directory)) {
            throw new DirectoryNotFoundException(message: $"The kernel directory '{directory}' does not exist.");
        }

        return new SdfKernelTreeLoad(
            bytecodeExtension: bytecodeExtension,
            cancellationToken: cancellationToken,
            compiler: compiler,
            directory: directory
        );
    }

    /// <summary>Removes and returns every kernel read or compiled since the last call.</summary>
    /// <returns>The ready kernels and their bytecode, possibly none.</returns>
    public IReadOnlyList<(SdfKernel Kernel, ReadOnlyMemory<byte> Bytecode)> TakeReady() {
        if (m_ready.IsEmpty) {
            return [];
        }

        var ready = new List<(SdfKernel, ReadOnlyMemory<byte>)>();

        while (m_ready.TryDequeue(result: out var item)) {
            ready.Add(item: item);
        }

        return ready;
    }
    /// <summary>Returns why the load failed once it has completed: each compile error's file, line and column, or a
    /// fault of the load itself; <see langword="null"/> while it runs or when every kernel compiled.</summary>
    /// <returns>The failure, or <see langword="null"/>.</returns>
    public string? FailureOf() {
        if (!m_completion.IsCompleted) {
            return null;
        }
        if (m_completion.Exception is { } fault) {
            return fault.InnerException?.Message ?? fault.Message;
        }
        if (m_completion.IsCanceled) {
            return "canceled";
        }

        return (m_errors.IsEmpty
            ? null
            : $"A kernel source does not compile: {string.Join(separator: "; ", values: m_errors)}");
    }
    /// <summary>Cancels the compiles still running and waits for them to stop.</summary>
    public void Dispose() {
        m_cancellation.Cancel();

        try {
            m_completion.Wait();
        } catch (AggregateException) {
            // A canceled or faulted load has nothing more to deliver.
        }

        m_cancellation.Dispose();
    }

    private async Task CompileAsync(IReadOnlyList<(SdfKernel Kernel, string Path)> sources, ShaderTarget target, ShaderCompiler compiler, CancellationToken cancellationToken) {
        var plans = new List<(SdfKernel Kernel, ShaderOutputPlan Plan)>(capacity: sources.Count);

        foreach (var (kernel, path) in sources) {
            try {
                plans.Add(item: (kernel, compiler.Plan(
                    stage: new ShaderStageSource(
                        EntryPoint: "CSMain",
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
        using var turns = new SemaphoreSlim(
            initialCount: Environment.ProcessorCount,
            maxCount: Environment.ProcessorCount
        );
        var compiles = plans.OrderBy(keySelector: static plan => (plan.Plan.RecordedDuration ?? TimeSpan.MaxValue)).Select(selector: async plan => {
            await turns.WaitAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

            try {
                var result = await compiler.CompileOutputAsync(
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
                m_ready.Enqueue(item: (plan.Kernel, result.Bytecode));
            } finally {
                turns.Release();
            }
        }).ToArray();

        await Task.WhenAll(tasks: compiles).ConfigureAwait(continueOnCapturedContext: false);
    }
}
