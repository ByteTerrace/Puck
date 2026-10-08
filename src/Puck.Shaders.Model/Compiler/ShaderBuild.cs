using System.Globalization;
using Puck.Abstractions;

namespace Puck.Shaders;

/// <summary>
/// A project's shader build: publishes the bytecode of every stage source beside it, compiling through
/// <see cref="ShaderCompiler"/> and its cache. <c>build/Shaders.targets</c> runs it through the build-only
/// <c>Puck.Shaders.Generator</c> host, once per shader project.
/// </summary>
/// <remarks>
/// <para>Each output is planned first (its closure collected and its cache key named) and is then current, in the cache,
/// or missing. A current output is left alone: its sidecar records the key, the cache holds that key, and the file's
/// bytes are the bytes its sidecar records. An output the cache holds is published from it and runs no compiler. The
/// missing ones compile concurrently, longest first by the durations the cache recorded for them, each on a core its
/// <see cref="IShaderCoreBroker"/> granted and only while the machine has the memory a compile needs free; a failure stops
/// admission and cancels the compiles still running.</para>
/// <para>A bytecode file and its <c>.hash</c> sidecar are published as one transaction under the project's publication
/// lock, which every reader of a pair takes too: the old sidecar is removed, the bytecode is moved into place whole, and
/// the new sidecar is moved in last. The sidecar is the pair's commit record, so a publication cut short leaves no
/// sidecar and the next build publishes again. The same lock covers the orphan sweep and <see cref="Check"/>.</para>
/// </remarks>
public sealed partial class ShaderBuild {
    // Scales a closure's bytes into an estimated compile time for an output no compile has been measured for, so a new
    // kernel is ordered among measured ones: DXIL's optimizer costs several times SPIR-V's on the same closure.
    private const double EstimatedMillisecondsPerByte = 0.01;
    private const double EstimatedDxilFactor = 7.0;
    // What one compile is held to need: the largest kernels' DXIL compiles peak near two gigabytes of working set. A
    // compile is admitted beside running ones only while that much is free for it and for each compile admitted too
    // recently to have grown yet.
    private const long CompileMemoryReserve = (((2L * 1024) * 1024) * 1024);

    private static readonly TimeSpan YoungCompile = TimeSpan.FromSeconds(seconds: 30);
    private static readonly TimeSpan MemoryRecheck = TimeSpan.FromSeconds(seconds: 2);

    private readonly ShaderCompiler m_compiler;
    private readonly string m_projectDirectory;
    private readonly string m_lockFile;
    private readonly TextWriter m_log;
    private readonly Func<long?>? m_availableMemory;

    /// <summary>Initializes a project's shader build.</summary>
    /// <param name="compiler">The compiler and cache every output compiles through.</param>
    /// <param name="projectDirectory">The project's directory, whose <c>Assets/Shaders</c> tree the orphan sweep
    /// walks and whose paths the messages name relative to.</param>
    /// <param name="lockFile">The project's publication lock.</param>
    /// <param name="log">Receives one line per message. A line in MSBuild's canonical error format is an error.</param>
    /// <param name="availableMemory">Reads the physical memory free now, in bytes, or <see langword="null"/> when it
    /// cannot (<see cref="Puck.Hosting.HostMemory.AvailablePhysicalBytes"/>); without it, compiles are bounded by cores
    /// alone.</param>
    public ShaderBuild(ShaderCompiler compiler, string projectDirectory, string lockFile, TextWriter log, Func<long?>? availableMemory = null) {
        ArgumentNullException.ThrowIfNull(argument: compiler);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: projectDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: lockFile);
        ArgumentNullException.ThrowIfNull(argument: log);
        m_compiler = compiler;
        m_projectDirectory = Path.GetFullPath(path: projectDirectory);
        m_lockFile = Path.GetFullPath(path: lockFile);
        m_log = log;
        m_availableMemory = availableMemory;
    }

    /// <summary>Gets the outputs the last <see cref="CompileAsync"/> ran a compiler for, in completion order.</summary>
    public IReadOnlyList<ShaderBuildOutput> Compiled => m_compiled;
    /// <summary>Gets the outputs the last <see cref="CompileAsync"/> published from the cache.</summary>
    public IReadOnlyList<ShaderBuildOutput> Restored => m_restored;
    /// <summary>Gets the order the last <see cref="CompileAsync"/> admitted its compiles in.</summary>
    public IReadOnlyList<ShaderBuildOutput> Admitted => m_admitted;

    private readonly List<ShaderBuildOutput> m_compiled = [];
    private readonly List<ShaderBuildOutput> m_restored = [];
    private readonly List<ShaderBuildOutput> m_admitted = [];

    /// <summary>Makes every output current: publishes what the cache holds and compiles the rest.</summary>
    /// <param name="outputs">The outputs.</param>
    /// <param name="cores">Grants the cores the compiles run on.</param>
    /// <param name="cancellationToken">Cancels the build and kills its compilers.</param>
    /// <returns><see langword="true"/> when every output is current.</returns>
    public async Task<bool> CompileAsync(IReadOnlyList<ShaderBuildOutput> outputs, IShaderCoreBroker cores, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(argument: outputs);
        ArgumentNullException.ThrowIfNull(argument: cores);
        m_compiled.Clear();
        m_restored.Clear();
        m_admitted.Clear();
        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        if (!SweepOrphans()) {
            return false;
        }

        var plans = PlanAll(outputs: outputs);

        if (plans is null) {
            return false;
        }

        var missing = new List<(ShaderBuildOutput Output, ShaderOutputPlan Plan)>();
        var current = 0;

        using (AcquireLock()) {
            foreach (var (output, plan) in plans) {
                if (IsCurrent(
                    output: output,
                    plan: plan
                )) {
                    current++;
                    continue;
                }

                var cached = m_compiler.ReadCached(plan: plan);

                if (cached is null) {
                    missing.Add(item: (output, plan));
                    continue;
                }

                Publish(
                    bytecode: cached,
                    output: output,
                    plan: plan
                );
                m_restored.Add(item: output);
            }
        }

        var succeeded = await CompileMissingAsync(
            cancellationToken: cancellationToken,
            cores: cores,
            missing: missing
        ).ConfigureAwait(continueOnCapturedContext: false);

        Log(line: string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"{plans.Count} shader output(s): {current} current, {m_restored.Count} published from the cache, {m_compiled.Count} compiled, in {System.Diagnostics.Stopwatch.GetElapsedTime(startingTimestamp: started).TotalSeconds:0.0} s."
        ));

        return succeeded;
    }
    /// <summary>Checks, compiling nothing and needing no toolchain or cache, that every output is on disk with the
    /// sidecar its current closure and options would publish, under the publication lock: what a pack that skips the
    /// build ships.</summary>
    /// <param name="outputs">The outputs.</param>
    /// <returns><see langword="true"/> when every output is current.</returns>
    public bool Check(IReadOnlyList<ShaderBuildOutput> outputs) {
        ArgumentNullException.ThrowIfNull(argument: outputs);

        if (!SweepOrphans()) {
            return false;
        }

        var plans = PlanAll(outputs: outputs);

        if (plans is null) {
            return false;
        }

        var valid = true;

        using (AcquireLock()) {
            foreach (var (output, plan) in plans) {
                var display = Display(path: output.OutputPath);

                if (!File.Exists(path: output.OutputPath)) {
                    valid = Error(message: $"Shader bytecode '{display}' is missing. Build normally before packing or publishing with --no-build.");
                    continue;
                }

                var sidecar = ReadSidecar(path: SidecarOf(output: output));

                if (sidecar is null) {
                    valid = Error(message: $"Shader bytecode '{display}' has no '.hash' sidecar. Build normally to refresh the bytecode and sidecar together.");
                    continue;
                }
                if (!string.Equals(a: sidecar.Value.Inputs, b: plan.Inputs, comparisonType: StringComparison.Ordinal)) {
                    valid = Error(message: $"Shader bytecode '{display}' is stale relative to its source, an include it reaches, or its compile options. Build normally to refresh the bytecode and '.hash' sidecar.");
                }
                if (!string.Equals(a: sidecar.Value.Bytecode, b: HashFile(path: output.OutputPath), comparisonType: StringComparison.Ordinal)) {
                    valid = Error(message: $"Shader bytecode '{display}' does not match its own '.hash' sidecar (its bytes changed without a build). Build normally to refresh the bytecode and '.hash' sidecar.");
                }
            }
        }

        return valid;
    }

    private List<(ShaderBuildOutput Output, ShaderOutputPlan Plan)>? PlanAll(IReadOnlyList<ShaderBuildOutput> outputs) {
        var plans = new List<(ShaderBuildOutput, ShaderOutputPlan)>(capacity: outputs.Count);
        var sources = new Dictionary<string, string>(comparer: PuckPaths.Comparer);
        var valid = true;

        foreach (var output in outputs) {
            try {
                if (!sources.TryGetValue(key: output.SourcePath, value: out var text)) {
                    text = File.ReadAllText(path: output.SourcePath);
                    sources[output.SourcePath] = text;
                }

                plans.Add(item: (output, m_compiler.Plan(
                    stage: new ShaderStageSource(
                        EntryPoint: ShaderCompiler.BuildEntryPointOf(stage: output.Stage),
                        Path: output.SourcePath,
                        Source: text,
                        Stage: output.Stage
                    ),
                    target: output.Target
                )));
            } catch (Exception exception) when ((exception is ShaderClosureRefusedException or IOException or UnauthorizedAccessException)) {
                valid = Error(message: exception.Message, path: output.SourcePath);
            }
        }

        return (valid ? plans : null);
    }
    private bool IsCurrent(ShaderBuildOutput output, ShaderOutputPlan plan) {
        if (!File.Exists(path: output.OutputPath)) {
            return false;
        }

        var sidecar = ReadSidecar(path: SidecarOf(output: output));

        return (
            (sidecar is { } recorded) &&
            string.Equals(a: recorded.Key, b: plan.Key, comparisonType: StringComparison.Ordinal) &&
            m_compiler.IsCached(plan: plan) &&
            string.Equals(a: recorded.Bytecode, b: HashFile(path: output.OutputPath), comparisonType: StringComparison.Ordinal)
        );
    }
    // Admits compiles onto the cores the broker grants, the longest first. The loop never waits on anything but its own
    // compiles and its one outstanding core request, so a request the host is slow to answer never stops a finished
    // compile from admitting the next one or giving its core back. Cores no running or queued compile can use go back at
    // once, a late grant included, and the build ends holding none: what it held is the host's again before it exits.
    private async Task<bool> CompileMissingAsync(List<(ShaderBuildOutput Output, ShaderOutputPlan Plan)> missing, IShaderCoreBroker cores, CancellationToken cancellationToken) {
        if (missing.Count == 0) {
            return true;
        }

        // Longest first: the slowest outputs bound the build, so they start while every core is free.
        var queue = new Queue<(ShaderBuildOutput Output, ShaderOutputPlan Plan)>(collection: missing
            .OrderByDescending(keySelector: static job => EstimateOf(plan: job.Plan))
            .ThenBy(keySelector: static job => job.Output.OutputPath, comparer: StringComparer.Ordinal));
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(token: cancellationToken);
        var running = new List<Task<bool>>();
        var admittedAt = new Dictionary<Task<bool>, long>();
        var held = await cores.RequestAsync(
            cancellationToken: cancellationToken,
            count: queue.Count
        ).ConfigureAwait(continueOnCapturedContext: false);
        Task<int>? request = null;
        var requesting = true;
        var failed = false;
        var waitedForMemory = false;

        if (held < 1) {
            throw new InvalidOperationException(message: "A core broker granted no core to a build's first request.");
        }
        try {
            while (true) {
                var memoryShort = false;

                while (!failed && (queue.Count > 0) && (running.Count < held)) {
                    if (!HasMemoryFor(admittedAt: admittedAt.Values, running: running.Count)) {
                        memoryShort = true;
                        if (!waitedForMemory) {
                            waitedForMemory = true;
                            Log(line: string.Create(provider: CultureInfo.InvariantCulture, handler: $"Shader compiles wait for memory: each reserves {(CompileMemoryReserve >> 20)} MB, and {running.Count} compile(s) run."));
                        }
                        break;
                    }

                    var (output, plan) = queue.Dequeue();
                    var compile = CompileOneAsync(
                        cancellationToken: stopping.Token,
                        output: output,
                        plan: plan
                    );

                    m_admitted.Add(item: output);
                    running.Add(item: compile);
                    admittedAt[compile] = System.Diagnostics.Stopwatch.GetTimestamp();
                }
                if (requesting && !failed && (queue.Count > 0) && (request is null)) {
                    request = cores.RequestAsync(
                        cancellationToken: stopping.Token,
                        count: queue.Count
                    );
                }

                // Every core no compile can use goes back now: with work queued, held never exceeds what runs plus
                // what waits, so the build keeps at least one core until its queue is empty.
                var usable = (running.Count + (failed ? 0 : queue.Count));

                if (held > usable) {
                    cores.Release(count: (held - usable));
                    held = usable;
                }
                if (running.Count == 0) {
                    break;
                }

                // Short of memory, the loop looks again shortly as well as when a compile finishes, since memory frees
                // outside this build too.
                IEnumerable<Task> waits = running;

                if (request is not null) {
                    waits = waits.Append(element: request);
                }
                if (memoryShort) {
                    waits = waits.Append(element: Task.Delay(cancellationToken: stopping.Token, delay: MemoryRecheck));
                }

                var finished = await Task.WhenAny(tasks: waits).ConfigureAwait(continueOnCapturedContext: false);

                if (finished == request) {
                    try {
                        held += await request.ConfigureAwait(continueOnCapturedContext: false);
                    } catch (OperationCanceledException) when (stopping.IsCancellationRequested) {
                    } catch (IOException exception) {
                        // The host stopped answering: the build finishes on the cores it holds.
                        requesting = false;
                        Log(line: $"Shader core requests ended ({exception.Message}); compiling on the {held} core(s) held.");
                    }
                    request = null;
                    continue;
                }

                if (finished is not Task<bool> completed) {
                    continue;
                }

                running.Remove(item: completed);
                _ = admittedAt.Remove(key: completed);
                if (!await completed.ConfigureAwait(continueOnCapturedContext: false) && !failed) {
                    failed = true;
                    await stopping.CancelAsync().ConfigureAwait(continueOnCapturedContext: false);
                }
            }
        } finally {
            // A request still outstanding is abandoned: cancelled, it ends at once, and a grant it already won goes back
            // with the rest. A broker answering it later gives that grant back itself.
            await stopping.CancelAsync().ConfigureAwait(continueOnCapturedContext: false);
            if (request is not null) {
                try {
                    held += await request.ConfigureAwait(continueOnCapturedContext: false);
                } catch (Exception exception) when ((exception is OperationCanceledException or IOException)) { }
            }
            if (held > 0) {
                cores.Release(count: held);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();

        return !failed;
    }
    // Whether one more compile fits: always when none runs, so the build makes progress, and otherwise while the memory
    // free covers the new compile and every running one admitted too recently to have grown.
    private bool HasMemoryFor(int running, IEnumerable<long> admittedAt) {
        if ((running == 0) || (m_availableMemory?.Invoke() is not { } available)) {
            return true;
        }

        var young = admittedAt.Count(predicate: static at => (System.Diagnostics.Stopwatch.GetElapsedTime(startingTimestamp: at) < YoungCompile));

        return (available >= (CompileMemoryReserve * (1 + young)));
    }
    private async Task<bool> CompileOneAsync(ShaderBuildOutput output, ShaderOutputPlan plan, CancellationToken cancellationToken) {
        ShaderOutputResult result;

        try {
            result = await m_compiler.CompileOutputAsync(
                cancellationToken: cancellationToken,
                plan: plan
            ).ConfigureAwait(continueOnCapturedContext: false);
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            return false;
        } catch (ShaderToolMissingException exception) {
            return Error(message: $"{exception.Message} Install the Vulkan SDK or Windows SDK, or pass /p:DxcCommand=\"path/to/dxc\".", path: output.SourcePath);
        }

        foreach (var diagnostic in result.Diagnostics) {
            var path = (diagnostic.Path ?? output.SourcePath);

            if (diagnostic.IsError) {
                _ = Error(
                    column: diagnostic.Column,
                    line: diagnostic.Line,
                    message: $"{diagnostic.Message} ({output.Target})",
                    path: path
                );
            } else {
                // A warning is reported as a message, as DXC's own output always was: its format is not MSBuild's, so a
                // warning never failed a build that treats warnings as errors.
                Log(line: string.Create(provider: CultureInfo.InvariantCulture, handler: $"{Display(path: path)}({diagnostic.Line},{diagnostic.Column}) [{output.Target}] {diagnostic.Message}"));
            }
        }
        if (!result.IsSuccess) {
            return (result.Diagnostics.Any(predicate: static diagnostic => diagnostic.IsError) ? false : Error(message: $"The {output.Target} compile produced no bytecode.", path: output.SourcePath));
        }

        using (AcquireLock()) {
            Publish(
                bytecode: result.Bytecode!,
                output: output,
                plan: plan
            );
        }
        lock (m_compiled) {
            m_compiled.Add(item: output);
        }
        Log(line: string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"Compiled {Display(path: output.OutputPath)} in {result.Elapsed.TotalSeconds:0.0} s."
        ));

        return true;
    }
    private static double EstimateOf(ShaderOutputPlan plan) =>
        (plan.RecordedDuration?.TotalMilliseconds ?? ((plan.ClosureBytes * EstimatedMillisecondsPerByte) * ((plan.Target == ShaderTarget.Dxil) ? EstimatedDxilFactor : 1.0)));
    private string Display(string path) {
        var relative = Path.GetRelativePath(
            path: path,
            relativeTo: m_projectDirectory
        );

        return ((Path.IsPathRooted(path: relative) || relative.StartsWith(comparisonType: StringComparison.Ordinal, value: ".."))
            ? PuckPaths.Normalize(path: path)
            : relative.Replace(newChar: '/', oldChar: Path.DirectorySeparatorChar));
    }
    // Writes one error in MSBuild's canonical format, which the build host logs as an error, and answers false.
    private bool Error(string message, string? path = null, int line = 0, int column = 0) {
        var origin = ((path is null)
            ? "Puck.Shaders"
            : ((line > 0)
                ? string.Create(provider: CultureInfo.InvariantCulture, handler: $"{path}({line},{Math.Max(val1: column, val2: 1)})")
                : path));

        Log(line: $"{origin}: error PUCKSHADER: {message.ReplaceLineEndings(replacementText: " ")}");

        return false;
    }
    private void Log(string line) {
        lock (m_log) {
            m_log.WriteLine(value: line);
        }
    }
}
