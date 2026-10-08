using Puck.Abstractions;
using Puck.Abstractions.Presentation;
using Puck.Assets;
using Puck.Hosting;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Puck.Shaders;

/// <summary>
/// Compiles HLSL stages with DXC, producing SPIR-V and DXIL candidates for both graphics and compute, through one
/// content-addressed cache that the runtime, the CLI and the build share.
/// </summary>
/// <remarks>
/// Every cache entry is one output, one stage compiled for one target (<see cref="ShaderTarget"/>), named by a key that
/// hashes what the tool reads and nothing about where it lies: the compiler revision, the step's tool and options, the
/// toolchain's identity, and the stage's closure laid out as its compile snapshot lays it out, each file by its path
/// relative to the deepest directory holding them all and its content hash. A closure compiled in one checkout is
/// therefore a hit in every other checkout of the same files, and an edit invalidates exactly the outputs whose closure
/// holds the edited file. The key also names the entry layout, so compilers on different commits that lay entries out
/// differently never read each other's entries from a shared cache. Each entry contains its key and the bytecode's SHA-256
/// and is published whole by one rename and never replaced while valid; readers verify it before use, and a damaged entry
/// is compiled again and replaced atomically. An entry's last write time is when a compile last used it: publishing
/// writes it, and every verified read stamps it, a publication a peer won included, so <see cref="Prune"/> removes
/// exactly what no compile has used since a cutoff.
/// </remarks>
public sealed partial class ShaderCompiler {
    // A content-identified note, not a header: this name is hashed into every compile identity, so a package built by another
    // compiler is a different identity and is rebuilt, never read. The ledger records the compiler's shape for review; no
    // fingerprint rides in the identity, because that would re-key every committed shader pin on each compiler edit.
    private const string CompilerVersion = "puck-shader-compiler-1";
    // The cache's directory of measured step durations, which a build orders its compiles by.
    private const string DurationsDirectory = "durations";
    // The longest include join, an including file's layout directory and the directive's text, that a snapshot keeps as
    // written: with a build root in a temporary directory of up to some 80 characters, it keeps every path DXC opens under
    // the Windows limit.
    private const int IncludeJoinBudget = 160;

    private static readonly UTF8Encoding SnapshotEncoding = new(encoderShouldEmitUTF8Identifier: false);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> m_gates = new(comparer: StringComparer.Ordinal);
    private readonly string m_cacheDirectory;
    private readonly IShaderProcessRunner m_processRunner;
    private readonly ShaderToolchain m_toolchain;

    /// <summary>Initializes a compiler over a cache directory, finding its tools in <paramref name="toolchainDirectory"/>
    /// or on the process search path.</summary>
    /// <param name="cacheDirectory">The cache directory, created when absent.</param>
    /// <param name="toolchainDirectory">The directory holding <c>dxc</c>, or <see langword="null"/> for the search path.</param>
    public ShaderCompiler(string cacheDirectory, string? toolchainDirectory = null)
        : this(
        cacheDirectory,
        new ShaderProcessRunner(),
        toolchainDirectory
    ) { }
    /// <summary>Creates a compiler over a supplied tool runner and cache directory.</summary>
    /// <param name="cacheDirectory">The cache directory, created when absent.</param>
    /// <param name="processRunner">Runs every tool invocation.</param>
    /// <param name="toolchainDirectory">The directory holding <c>dxc</c>, or <see langword="null"/> for the search path.</param>
    public ShaderCompiler(string cacheDirectory, IShaderProcessRunner processRunner, string? toolchainDirectory = null)
        : this(
        cacheDirectory: cacheDirectory,
        processRunner: processRunner,
        toolchain: new ShaderToolchain(directory: toolchainDirectory)
    ) { }
    /// <summary>Creates a compiler over a supplied toolchain, such as the one a build's <c>DxcCommand</c> names
    /// (<see cref="ShaderToolchain.OfCommand"/>).</summary>
    /// <param name="cacheDirectory">The cache directory, created when absent.</param>
    /// <param name="toolchain">The toolchain.</param>
    /// <param name="processRunner">Runs every tool invocation, or <see langword="null"/> to start each as a child
    /// process.</param>
    public ShaderCompiler(string cacheDirectory, ShaderToolchain toolchain, IShaderProcessRunner? processRunner = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        ArgumentNullException.ThrowIfNull(toolchain);
        m_cacheDirectory = Path.GetFullPath(path: cacheDirectory);
        Directory.CreateDirectory(path: m_cacheDirectory);
        m_processRunner = (processRunner ?? new ShaderProcessRunner());
        m_toolchain = toolchain;
    }

    /// <summary>Gets the cache every compiler shares unless it is handed another: <c>shaders</c> in the per-user Puck
    /// directory (<see cref="PuckUserDirectory"/>). The build compiles into it too, so valid entries are reused across
    /// checkouts while their closure, recipe and toolchain are unchanged.</summary>
    public static string DefaultCacheDirectory => PuckUserDirectory.Resolve(name: "shaders");
    /// <summary>Gets the cache directory used by this compiler.</summary>
    public string CacheDirectory => m_cacheDirectory;
    /// <summary>Gets the resolved toolchain configuration.</summary>
    public ShaderToolchain Toolchain => m_toolchain;

    public CompiledShader Compile(ShaderCompilationRequest descriptor) =>
        CompileAsync(descriptor).GetAwaiter().GetResult();
    /// <summary>Compiles every stage in a descriptor away from the presentation thread.</summary>
    public Task<CompiledShader> CompileAsync(ShaderCompilationRequest descriptor, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(descriptor);
        return Task.Run(
            () => CompileCoreAsync(
                cancellationToken: cancellationToken,
                descriptor: descriptor
            ),
            cancellationToken
        );
    }
    /// <summary>Plans one output of a stage source the build compiles: collects the stage's closure from disk and names
    /// the cache entry it compiles to. Nothing runs.</summary>
    /// <param name="stage">The stage source.</param>
    /// <param name="target">The output's target.</param>
    /// <param name="tier">The quality tier, or <see langword="null"/> for the variant no tier names.</param>
    /// <returns>The plan.</returns>
    /// <exception cref="ShaderClosureRefusedException">The closure is refused; the code names why.</exception>
    public ShaderOutputPlan Plan(ShaderStageSource stage, ShaderTarget target, QualityTier? tier = null) {
        ArgumentNullException.ThrowIfNull(argument: stage);

        var work = Prepare(
            generated: null,
            readInclude: null,
            stage: stage,
            tier: tier
        );
        var inputs = InputsOf(
            target: target,
            work: work
        );

        return new ShaderOutputPlan(
            inputs: inputs,
            key: KeyOf(inputs: inputs),
            recordedDuration: ReadDuration(
                target: target,
                work: work
            ),
            target: target,
            work: work
        );
    }
    /// <summary>Indicates whether the cache holds a verified copy of a plan's output. An entry it verifies is stamped as
    /// used now (<see cref="Prune"/>).</summary>
    /// <param name="plan">The plan.</param>
    /// <returns><see langword="true"/> when the entry's key and bytecode digest match.</returns>
    public bool IsCached(ShaderOutputPlan plan) {
        ArgumentNullException.ThrowIfNull(argument: plan);

        return (ReadCached(plan: plan) is not null);
    }
    /// <summary>Reads a plan's output from the cache, stamping the entry as used now (<see cref="Prune"/>).</summary>
    /// <param name="plan">The plan.</param>
    /// <returns>The verified bytecode, or <see langword="null"/> when the entry is absent or damaged.</returns>
    public byte[]? ReadCached(ShaderOutputPlan plan) {
        ArgumentNullException.ThrowIfNull(argument: plan);

        return TryRead(path: EntryPath(
            key: plan.Key,
            target: plan.Target
        ));
    }
    /// <summary>Compiles one planned output, or reads it when the cache holds it (a peer may have published it since it
    /// was planned), away from the calling thread.</summary>
    /// <param name="plan">The plan.</param>
    /// <param name="cancellationToken">Cancels the compile and kills its tool.</param>
    /// <returns>The output.</returns>
    public Task<ShaderOutputResult> CompileOutputAsync(ShaderOutputPlan plan, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(argument: plan);

        return Task.Run(
            function: async () => {
                Work.Count(kind: Requests);
                var started = Stopwatch.GetTimestamp();
                var outcome = await CompileStageAsync(
                    cancellationToken: cancellationToken,
                    plannedKeys: [plan.Key],
                    targets: [plan.Target],
                    work: plan.Work
                ).ConfigureAwait(continueOnCapturedContext: false);

                if (!outcome.RanTool) {
                    Work.Count(kind: CacheHits);
                }

                return new ShaderOutputResult(
                    Bytecode: outcome.Bytecode[0],
                    Compiled: outcome.RanTool,
                    Diagnostics: outcome.Diagnostics,
                    Elapsed: Stopwatch.GetElapsedTime(startingTimestamp: started)
                );
            },
            cancellationToken: cancellationToken
        );
    }

    private async Task<CompiledShader> CompileCoreAsync(ShaderCompilationRequest descriptor, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        Work.Count(kind: Requests);
        ShaderSourceClosure closure;

        // The closure is collected before the cache is consulted, so a missing include is refused even when a warm
        // entry holds the bytecode it once produced.
        try {
            closure = ShaderSourceClosure.Collect(
                generated: descriptor.GeneratedIncludes,
                limits: ShaderSourceLimits.Default,
                sources: descriptor.Stages.Select(selector: static stage => (stage.Path, stage.Source)).ToArray()
            );
        } catch (ShaderClosureRefusedException exception) {
            return new CompiledShader(
                descriptor.Name,
                FirstPath(descriptor: descriptor),
                ShaderSourceClosure.HashOf(text: SerializeDescriptor(descriptor: descriptor)),
                new Dictionary<ShaderStage, ReadOnlyMemory<byte>>(),
                new Dictionary<ShaderStage, ReadOnlyMemory<byte>>(),
                [new ShaderDiagnostic(
                    0,
                    0,
                    exception.Message,
                    true,
                    descriptor.Stages[0].Stage,
                    Path.GetFullPath(path: descriptor.Stages[0].Path)
                )]
            );
        }
        var identity = Identify(
            closure: closure,
            request: descriptor
        );
        var sourceHash = ShaderSourceClosure.HashOf(text: SerializeDescriptor(descriptor: descriptor));
        var spirv = new Dictionary<ShaderStage, ReadOnlyMemory<byte>>();
        var dxil = new Dictionary<ShaderStage, ReadOnlyMemory<byte>>();
        var diagnostics = new List<ShaderDiagnostic>();
        var ranTool = false;

        foreach (var stage in descriptor.Stages) {
            cancellationToken.ThrowIfCancellationRequested();
            // Each stage keys its own closure, read from the request's closure so nothing is read twice.
            var work = Prepare(
                generated: descriptor.GeneratedIncludes,
                readInclude: path => (closure.Contents.TryGetValue(
                    key: path,
                    value: out var text
                ) ? text : null),
                stage: stage,
                tier: descriptor.Tier
            );
            var outcome = await CompileStageAsync(
                cancellationToken: cancellationToken,
                targets: [ShaderTarget.Spirv, ShaderTarget.Dxil],
                work: work
            ).ConfigureAwait(continueOnCapturedContext: false);

            ranTool |= outcome.RanTool;
            diagnostics.AddRange(collection: outcome.Diagnostics);
            if (outcome.Bytecode[0] is { } stageSpirv) { spirv[stage.Stage] = stageSpirv; }
            if (outcome.Bytecode[1] is { } stageDxil) { dxil[stage.Stage] = stageDxil; }
            if (outcome.Diagnostics.Any(predicate: static diagnostic => diagnostic.IsError)) {
                return new CompiledShader(
                    descriptor.Name,
                    FirstPath(descriptor: descriptor),
                    sourceHash,
                    spirv,
                    dxil,
                    diagnostics,
                    identity
                );
            }
        }
        if (!ranTool) {
            Work.Count(kind: CacheHits);
        }

        return new CompiledShader(
            descriptor.Name,
            FirstPath(descriptor: descriptor),
            sourceHash,
            spirv,
            dxil,
            diagnostics,
            identity
        );
    }
    // One stage's outputs: each target's entry read from the cache, or compiled over one snapshot of the stage's closure
    // and published under its key. A per-key gate makes concurrent compiles of one output in this process share one tool
    // run; separate processes may both compile an output, and the first to publish it wins.
    private async Task<StageOutcome> CompileStageAsync(StageWork work, IReadOnlyList<ShaderTarget> targets, CancellationToken cancellationToken, string[]? plannedKeys = null) {
        cancellationToken.ThrowIfCancellationRequested();
        var keys = targets.Select(selector: target => KeyOf(inputs: InputsOf(
            target: target,
            work: work
        ))).ToArray();

        if ((plannedKeys is not null) && !plannedKeys.SequenceEqual(second: keys, comparer: StringComparer.Ordinal)) {
            throw new IOException(message: "The shader toolchain changed after this output was planned; build again with a stable toolchain.");
        }
        var bytecode = new byte[]?[targets.Count];

        if (TryReadAll(
            bytecode: bytecode,
            keys: keys,
            targets: targets
        )) {
            return new StageOutcome(
                Bytecode: bytecode,
                Diagnostics: [],
                RanTool: false
            );
        }

        var gates = keys.Distinct(comparer: StringComparer.Ordinal).Order(comparer: StringComparer.Ordinal).Select(selector: key => m_gates.GetOrAdd(
            key: key,
            valueFactory: static _ => new SemaphoreSlim(
                initialCount: 1,
                maxCount: 1
            )
        )).ToArray();
        var entered = 0;

        try {
            foreach (var gate in gates) {
                await gate.WaitAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
                entered++;
            }
            if (TryReadAll(
                bytecode: bytecode,
                keys: keys,
                targets: targets
            )) {
                return new StageOutcome(
                    Bytecode: bytecode,
                    Diagnostics: [],
                    RanTool: false
                );
            }

            return await BuildAsync(
                bytecode: bytecode,
                cancellationToken: cancellationToken,
                keys: keys,
                targets: targets,
                work: work
            ).ConfigureAwait(continueOnCapturedContext: false);
        } finally {
            for (var index = 0; (index < entered); index++) {
                gates[index].Release();
            }
        }
    }
    private bool TryReadAll(IReadOnlyList<ShaderTarget> targets, string[] keys, byte[]?[] bytecode) {
        var complete = true;

        for (var index = 0; (index < targets.Count); index++) {
            bytecode[index] ??= TryRead(path: EntryPath(
                key: keys[index],
                target: targets[index]
            ));
            complete &= (bytecode[index] is not null);
        }

        return complete;
    }
    private async Task<StageOutcome> BuildAsync(StageWork work, IReadOnlyList<ShaderTarget> targets, string[] keys, byte[]?[] bytecode, CancellationToken cancellationToken) {
        var stage = work.Stage;
        var diagnostics = new List<ShaderDiagnostic>();
        // In the temporary directory, not the cache, and short: every path below it reaches DXC, which refuses paths past
        // the Windows 260-character limit, and a cache may lie anywhere. The per-key gate already serializes builds of one
        // key, so 48 random bits never collide in practice.
        var buildId = Guid.NewGuid().ToString(format: "N")[..12];
        var buildRoot = Path.Combine(
            path1: Path.GetTempPath(),
            path2: ("puck-dxc-" + buildId)
        );

        Directory.CreateDirectory(path: buildRoot);
        try {
            foreach (var dependency in work.Closure.Contents) {
                var dependencyPath = SnapshotPath(
                    path: dependency.Key,
                    root: buildRoot,
                    work: work
                );

                Directory.CreateDirectory(path: Path.GetDirectoryName(path: dependencyPath)!);
                await File.WriteAllTextAsync(
                    dependencyPath,
                    SnapshotText(
                        path: dependency.Key,
                        text: dependency.Value,
                        work: work
                    ),
                    SnapshotEncoding,
                    cancellationToken
                ).ConfigureAwait(continueOnCapturedContext: false);
            }

            var snapshotPath = SnapshotPath(
                path: stage.Path,
                root: buildRoot,
                work: work
            );

            Directory.CreateDirectory(path: Path.GetDirectoryName(path: snapshotPath)!);
            await File.WriteAllTextAsync(
                snapshotPath,
                SnapshotText(
                    path: stage.Path,
                    text: stage.Source,
                    work: work
                ),
                SnapshotEncoding,
                cancellationToken
            ).ConfigureAwait(continueOnCapturedContext: false);

            // The stage's own directory, then the build root every snapshotted include directive names its include from.
            string[] includeDirectories = [Path.GetDirectoryName(path: snapshotPath)!, buildRoot];
            var diagnosticPaths = new Dictionary<string, string>(comparer: StringComparer.OrdinalIgnoreCase) { [snapshotPath] = Path.GetFullPath(path: stage.Path) };

            foreach (var dependency in work.Closure.Contents.Keys) {
                diagnosticPaths[SnapshotPath(
                    path: dependency,
                    root: buildRoot,
                    work: work
                )] = dependency;
            }

            for (var index = 0; (index < targets.Count); index++) {
                if (bytecode[index] is not null) { continue; }
                cancellationToken.ThrowIfCancellationRequested();

                var target = targets[index];
                var output = Path.Combine(
                    path1: buildRoot,
                    path2: $"{StageName(stage: stage.Stage)}.{ExtensionOf(target: target)}.tmp"
                );
                var started = Stopwatch.GetTimestamp();
                var run = await RunStepAsync(
                    cancellationToken: cancellationToken,
                    includeDirectories: includeDirectories,
                    input: snapshotPath,
                    output: output,
                    step: work.Steps[((int)target)]
                ).ConfigureAwait(continueOnCapturedContext: false);
                // The tool's own time, measured before the toolchain is read again, orders later builds' compiles.
                var elapsed = Stopwatch.GetElapsedTime(startingTimestamp: started);

                if (!string.Equals(a: keys[index], b: KeyOf(inputs: InputsOf(target: target, work: work)), comparisonType: StringComparison.Ordinal)) {
                    throw new IOException(message: "The shader toolchain changed during compilation; no output is cached or published.");
                }
                var stepDiagnostics = ParseDxcDiagnostics(
                    paths: diagnosticPaths,
                    stage: stage,
                    text: ((run.Stdout + "\n") + run.Stderr)
                );

                if (
                    (run.ExitCode != 0) ||
                    !File.Exists(path: output) ||
                    (new FileInfo(fileName: output).Length == 0)
                ) {
                    AddToolFailure(
                        stepDiagnostics,
                        $"dxc ({((target == ShaderTarget.Spirv) ? "SPIR-V" : "DXIL")})",
                        run.ExitCode,
                        ((run.Stdout + "\n") + run.Stderr)
                    );
                }
                diagnostics.AddRange(collection: stepDiagnostics);
                if (stepDiagnostics.Any(predicate: static diagnostic => diagnostic.IsError)) {
                    continue;
                }

                var bytes = File.ReadAllBytes(path: output);
                var entry = EntryPath(
                    key: keys[index],
                    target: target
                );

                bytecode[index] = bytes;
                PublishCached(
                    bytes: bytes,
                    path: entry
                );
                RecordDuration(
                    elapsed: elapsed,
                    target: target,
                    work: work
                );
            }

            return new StageOutcome(
                Bytecode: bytecode,
                Diagnostics: diagnostics,
                RanTool: true
            );
        } finally {
            try {
                Directory.Delete(
                path: buildRoot,
                recursive: true
            );
            } catch (IOException) { }
        }
    }
    // Collects one stage's closure and the layout its snapshot gives it: every file by its path below the deepest
    // directory holding them all, with its content hash. The layout is what the stage's cache keys hash, so it names
    // nothing of where the checkout or the cache lies.
    private static StageWork Prepare(ShaderStageSource stage, QualityTier? tier, IReadOnlyDictionary<string, string>? generated, Func<string, string?>? readInclude) {
        var closure = ShaderSourceClosure.Collect(
            generated: generated,
            limits: ShaderSourceLimits.Default,
            readInclude: readInclude,
            sources: [(stage.Path, stage.Source)]
        );
        var snapshotBase = SnapshotBase(paths: closure.Includes.Select(selector: static include => include.Path).Prepend(element: stage.Path));
        var root = Path.GetFullPath(path: (Path.DirectorySeparatorChar + "snapshot"));

        string LayoutPath(string path) =>
            Path.GetRelativePath(
                path: SnapshotPath(
                    path: path,
                    root: root,
                    snapshotBase: snapshotBase
                ),
                relativeTo: root
            ).Replace(
                newChar: '/',
                oldChar: '\\'
            );
        var layout = new StringBuilder();

        layout.Append(value: "input|").Append(value: LayoutPath(path: stage.Path)).Append(value: '|').Append(value: closure.Sources[0].ContentHash).Append(value: '\n');
        foreach (var (path, hash) in closure.Includes.Select(selector: include => (Path: LayoutPath(path: include.Path), Hash: include.ContentHash)).OrderBy(
            keySelector: static include => include.Path,
            comparer: StringComparer.Ordinal
        )) {
            layout.Append(value: "include|").Append(value: path).Append(value: '|').Append(value: hash).Append(value: '\n');
        }

        // Where each file lies in a build's snapshot: at its layout path, so the snapshot is the closure's own tree and
        // every include directive resolves as written, as it does in place.
        var snapshot = closure.Includes.Select(selector: static include => include.Path).Prepend(element: stage.Path)
            .Select(selector: static path => Path.GetFullPath(path: path))
            .Distinct(comparer: PuckPaths.Comparer)
            .ToDictionary(
                comparer: PuckPaths.Comparer,
                elementSelector: LayoutPath,
                keySelector: static path => path
            );

        return new StageWork(
            Closure: closure,
            Layout: layout.ToString(),
            Snapshot: snapshot,
            Stage: stage,
            Steps: StepsOf(
                entryPoint: stage.EntryPoint,
                stage: stage.Stage,
                tier: tier
            )
        );
    }
    // Everything one output reads apart from the toolchain: the compiler revision, the step's tool and options, and the
    // stage's snapshot layout.
    private static string InputsOf(StageWork work, ShaderTarget target) {
        var step = work.Steps[((int)target)];
        var builder = new StringBuilder(value: CompilerVersion).Append(value: '\n').Append(value: step.Tool).Append(value: '\n');

        foreach (var option in step.Options) {
            builder.Append(value: option).Append(value: '\n');
        }
        // Rewriting a directive changes DXIL bytes. Hash the transformed text too whenever the snapshot changes it,
        // so an entry produced by a different snapshot transformation cannot masquerade as this output.
        foreach (var (path, text) in work.Closure.Contents.Prepend(element: new KeyValuePair<string, string>(key: work.Stage.Path, value: work.Stage.Source)).OrderBy(keySelector: file => work.Snapshot[Path.GetFullPath(path: file.Key)], comparer: StringComparer.Ordinal)) {
            var snapshot = SnapshotText(path: path, text: text, work: work);

            if (!string.Equals(a: text, b: snapshot, comparisonType: StringComparison.Ordinal)) {
                builder.Append(value: "snapshot|").Append(value: work.Snapshot[Path.GetFullPath(path: path)]).Append(value: '|').Append(value: ShaderSourceClosure.HashOf(text: snapshot)).Append(value: '\n');
            }
        }

        return ShaderSourceClosure.HashOf(text: builder.Append(value: work.Layout).ToString());
    }
    // Every key asks the toolchain for its identity, which hashes the files once and re-reads only their metadata after
    // that (ShaderToolchain.Identity), so a dxc replaced under a long-lived compiler keys its next compile afresh. The
    // entry layout is keyed too, so compilers that lay entries out differently, on checkouts of different commits sharing
    // one cache, read and write different names and never take one another's entries for bytecode.
    private string KeyOf(string inputs) => ShaderSourceClosure.HashOf(text: ((((inputs + "|") + m_toolchain.Identity) + "|") + CacheEntryLayout));
    private string EntryPath(string key, ShaderTarget target) => Path.Combine(
        path1: m_cacheDirectory,
        path2: $"{key}.{ExtensionOf(target: target)}"
    );
    // A step's measured duration is named by the stage source's file name and the step's options, not by its content, so
    // the next build of an edited kernel still knows it is long.
    private string DurationPath(StageWork work, ShaderTarget target) => Path.Combine(
        path1: m_cacheDirectory,
        path2: DurationsDirectory,
        path3: ShaderSourceClosure.HashOf(text: ((Path.GetFileName(path: work.Stage.Path) + "\n") + string.Join(
            separator: '\n',
            values: work.Steps[((int)target)].Options
        )))
    );
    private TimeSpan? ReadDuration(StageWork work, ShaderTarget target) {
        try {
            var path = DurationPath(
                target: target,
                work: work
            );

            if (!File.Exists(path: path)) {
                return null;
            }

            var text = Encoding.UTF8.GetString(bytes: AtomicFile.ReadAllBytes(path: path)).Trim();

            Stamp(path: path);

            return (long.TryParse(
                provider: CultureInfo.InvariantCulture,
                result: out var milliseconds,
                s: text,
                style: NumberStyles.None
            ) ? TimeSpan.FromMilliseconds(milliseconds: milliseconds) : null);
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            return null;
        }
    }
    private void RecordDuration(StageWork work, ShaderTarget target, TimeSpan elapsed) {
        var path = DurationPath(
            target: target,
            work: work
        );

        try {
            Directory.CreateDirectory(path: Path.GetDirectoryName(path: path)!);
            AtomicFile.WriteAllText(
                contents: ((long)elapsed.TotalMilliseconds).ToString(provider: CultureInfo.InvariantCulture),
                path: path
            );
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            // A duration orders a later build's compiles; losing one costs only that order.
        }
    }
    private async Task<ChildProcessResult> RunToolAsync(string name, IReadOnlyList<string> args, CancellationToken cancellationToken) {
        var executable = m_toolchain.Resolve(name: name);

        try {
            return await m_processRunner.RunAsync(
            arguments: args,
            cancellationToken: cancellationToken,
            fileName: executable
        ).ConfigureAwait(continueOnCapturedContext: false);
        } catch (System.ComponentModel.Win32Exception) {
            throw new ShaderToolMissingException(
            name,
            m_toolchain.Directory
        );
        }
    }
    // The deepest directory holding every file a build snapshots, or null when the files span volumes. A snapshot
    // mirrors only the tree below it, so the paths handed to native tools stay short however deep the checkout sits.
    private static string? SnapshotBase(IEnumerable<string> paths) {
        string? common = null;

        foreach (var path in paths) {
            var directory = (Path.GetDirectoryName(path: Path.GetFullPath(path: path)) ?? string.Empty);

            if (common is null) {
                common = directory;
                continue;
            }
            while (!ShaderSourceClosure.IsWithin(
                directory: common,
                path: directory
            )) {
                common = Path.GetDirectoryName(path: common);
                if (common is null) { return null; }
            }
        }
        return common;
    }
    // The text a snapshot holds of a source: the source's own text, each include directive as written, so the snapshot
    // compiles to the bytes the source compiles to in place. DXC's output depends on how a directive spells its include,
    // not only on what it reaches. DXC joins a relative include to the including file's directory before it normalizes
    // the result, so a directive whose join within the layout passes IncludeJoinBudget (a deep source including one that
    // climbs back out) could pass the Windows path limit and not be found; that directive alone names its include by its
    // snapshot path instead, relative to the build root, which the compile's include directories resolve. The choice
    // reads only the layout, never where the snapshot lies, so a snapshot compiles to the same bytes wherever it is taken.
    // Lines do not move, and a diagnostic's path maps back through the snapshot.
    private static string SnapshotText(string text, string path, StageWork work) {
        var directory = DirectoryOf(layoutPath: work.Snapshot[Path.GetFullPath(path: path)]);

        return ShaderSourceClosure.WithIncludes(
            map: (include, written) => (
                ((Path.IsPathRooted(path: written) || (((directory.Length + 1) + written.Length) > IncludeJoinBudget)) && work.Snapshot.TryGetValue(key: include, value: out var snapshot))
                    ? snapshot
                    : written
            ),
            path: path,
            text: text
        );
    }
    // Where a closure file lies in one build's snapshot under its root.
    private static string SnapshotPath(string root, string path, StageWork work) =>
        Path.GetFullPath(path: Path.Combine(path1: root, path2: work.Snapshot[Path.GetFullPath(path: path)]));
    private static string SnapshotPath(string root, string? snapshotBase, string path) {
        var fullPath = Path.GetFullPath(path: path);

        if (
            (snapshotBase is not null) &&
            ShaderSourceClosure.IsWithin(
            directory: snapshotBase,
            path: fullPath
        )
        ) {
            return Path.Combine(
                path1: root,
                path2: "src",
                path3: Path.GetRelativePath(
                    path: fullPath,
                    relativeTo: snapshotBase
                )
            );
        }
        var volume = (Path.GetPathRoot(path: fullPath) ?? string.Empty);
        var relative = ((volume.Length == 0)
            ? fullPath
            : Path.GetRelativePath(
                path: fullPath,
                relativeTo: volume
            )
        );

        if (
            Path.IsPathRooted(path: relative) ||
            relative.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: (".." + Path.DirectorySeparatorChar)
        )
        ) {
            throw new InvalidDataException(message: $"Shader source path '{path}' cannot be represented in a compile snapshot.");
        }

        // Keep volume identity in the snapshot. Without it, equal relative paths
        // on separate drives/UNC roots can overwrite each other's includes.
        var volumeKey = ShaderSourceClosure.HashOf(text: volume.ToUpperInvariant())[..16];

        return Path.Combine(
            path1: root,
            path2: ("volume-" + volumeKey),
            path3: relative
        );
    }
    private static string SerializeDescriptor(ShaderCompilationRequest descriptor) =>
        string.Join(
            separator: "\n",
            values: descriptor.Stages.Select(selector: static stage => $"{stage.Stage}|{stage.Path}|{stage.EntryPoint}|{stage.Source}")
        );
    private static string FirstPath(ShaderCompilationRequest descriptor) => descriptor.Stages[0].Path;
    private static string ExtensionOf(ShaderTarget target) => ((target == ShaderTarget.Spirv) ? "spv" : "dxil");
    private static string StageName(ShaderStage stage) => stage switch { ShaderStage.Vertex => "vert", ShaderStage.Fragment => "frag", _ => "comp" };
    private static void AddToolFailure(List<ShaderDiagnostic> diagnostics, string tool, int exitCode, string output) {
        var detail = string.Join(
            separator: " ",
            values: output.Split(
                options: StringSplitOptions.RemoveEmptyEntries,
                separator: ["\r\n", "\n", "\r"]
            ).Select(selector: static line => line.Trim()).Where(predicate: static line => (line.Length != 0)).Take(count: 4)
        );

        diagnostics.Add(item: new ShaderDiagnostic(
            0,
            0,
            (string.IsNullOrEmpty(value: detail)
            ? $"{tool} exited with code {exitCode}."
            : $"{tool} exited with code {exitCode}: {detail}"),
            true
        ));
    }
    private static List<ShaderDiagnostic> ParseDxcDiagnostics(string text, ShaderStageSource stage, IReadOnlyDictionary<string, string> paths) {
        var result = new List<ShaderDiagnostic>();

        foreach (var line in text.Split('\n')) {
            var match = DxcDiagnosticPattern().Match(input: line.TrimEnd(trimChar: '\r'));

            if (
                match.Success &&
                int.TryParse(
                match.Groups[2].Value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var lineNumber
            )
            ) {
                result.Add(item: new ShaderDiagnostic(
                    lineNumber,
                    int.Parse(
                        match.Groups[3].Value,
                        CultureInfo.InvariantCulture
                    ),
                    match.Groups[5].Value.Trim(),
                    (match.Groups[4].Value == "error"),
                    stage.Stage,
                    DiagnosticPath(
                        match.Groups[1].Value,
                        stage,
                        paths
                    )
                ));
            }
        }
        return result;
    }
    private static string DiagnosticPath(string reported, ShaderStageSource stage, IReadOnlyDictionary<string, string> paths) {
        var candidate = reported.Trim();

        if (paths.TryGetValue(
            key: candidate,
            value: out var authored
        )) { return authored; }
        try {
            var full = Path.GetFullPath(path: candidate);

            if (paths.TryGetValue(
                key: full,
                value: out authored
            )) { return authored; }
        } catch (ArgumentException) { }
        return stage.Path;
    }
    [GeneratedRegex(@"^(.*):(\d+):(\d+):\s*(error|warning):\s*(.*)$")]
    private static partial Regex DxcDiagnosticPattern();
    private static string DirectoryOf(string layoutPath) {
        var slash = layoutPath.LastIndexOf(value: '/');

        return ((slash < 0) ? string.Empty : layoutPath[..slash]);
    }

    // What compiling one stage reads: its closure, its layout (every file by its path relative to the others and its
    // content hash, which the output's key hashes), where each file lies in a build's snapshot, by full path, and the
    // steps StepsOf gives it.
    internal sealed record StageWork(
        ShaderStageSource Stage,
        ShaderSourceClosure Closure,
        string Layout,
        IReadOnlyDictionary<string, string> Snapshot,
        IReadOnlyList<ShaderCompileStep> Steps
    );

    private sealed record StageOutcome(byte[]?[] Bytecode, IReadOnlyList<ShaderDiagnostic> Diagnostics, bool RanTool);
}
