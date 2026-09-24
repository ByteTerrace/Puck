using Puck.Assets;
using Puck.Hosting;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Puck.Shaders;

/// <summary>
/// Compiles HLSL stages with DXC, producing SPIR-V and DXIL candidates for both graphics and compute.
/// </summary>
public sealed partial class ShaderCompiler {
    private const string CompilerVersion = "puck-shader-compiler-4";

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> m_gates = new(comparer: StringComparer.Ordinal);
    private readonly string m_cacheDirectory;
    private readonly IShaderProcessRunner m_processRunner;
    private readonly ShaderToolchain m_toolchain;

    public ShaderCompiler(string cacheDirectory, string? toolchainDirectory = null)
        : this(
        cacheDirectory,
        new ShaderProcessRunner(),
        toolchainDirectory
    ) { }

    internal ShaderCompiler(string cacheDirectory, IShaderProcessRunner processRunner, string? toolchainDirectory = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        ArgumentNullException.ThrowIfNull(processRunner);
        m_cacheDirectory = Path.GetFullPath(path: cacheDirectory);
        Directory.CreateDirectory(path: m_cacheDirectory);
        m_processRunner = processRunner;
        m_toolchain = new ShaderToolchain(directory: toolchainDirectory);
    }

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
        var cacheKey = CreateCacheKey(
            descriptor: descriptor,
            identity: identity
        );
        var gate = m_gates.GetOrAdd(
            key: cacheKey,
            valueFactory: static _ => new SemaphoreSlim(
                initialCount: 1,
                maxCount: 1
            )
        );

        await gate.WaitAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        try {
            return await CompileCoreLockedAsync(
            cacheKey: cacheKey,
            cancellationToken: cancellationToken,
            closure: closure,
            descriptor: descriptor,
            identity: identity
        ).ConfigureAwait(continueOnCapturedContext: false);
        } finally { gate.Release(); }
    }
    private async Task<CompiledShader> CompileCoreLockedAsync(
        ShaderCompilationRequest descriptor,
        ShaderSourceClosure closure,
        ShaderCompileIdentity identity,
        string cacheKey,
        CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        var stem = Path.Combine(
            path1: m_cacheDirectory,
            path2: cacheKey
        );
        var sourceHash = ShaderSourceClosure.HashOf(text: SerializeDescriptor(descriptor: descriptor));
        var completePath = (stem + ".complete");
        var cachedSpirv = new Dictionary<ShaderStage, ReadOnlyMemory<byte>>();
        var cachedDxil = new Dictionary<ShaderStage, ReadOnlyMemory<byte>>();
        var allCached = File.Exists(path: completePath);

        foreach (var stage in descriptor.Stages) {
            var spirvPath = BytecodePath(
                stem,
                stage.Stage,
                "spv"
            );
            var dxilPath = BytecodePath(
                stem,
                stage.Stage,
                "dxil"
            );

            if (
                !allCached ||
                !File.Exists(path: spirvPath) ||
                !File.Exists(path: dxilPath)
            ) { allCached = false; break; }
            cachedSpirv[stage.Stage] = AtomicFile.ReadAllBytes(path: spirvPath);
            cachedDxil[stage.Stage] = AtomicFile.ReadAllBytes(path: dxilPath);
        }
        if (allCached) {
            Work.Count(kind: CacheHits);
            return new CompiledShader(
                descriptor.Name,
                FirstPath(descriptor: descriptor),
                sourceHash,
                cachedSpirv,
                cachedDxil,
                [],
                identity
            );
        }

        var diagnostics = new List<ShaderDiagnostic>();
        var spirv = new Dictionary<ShaderStage, ReadOnlyMemory<byte>>();
        var dxil = new Dictionary<ShaderStage, ReadOnlyMemory<byte>>();
        // Short, because every path below it reaches native tools that refuse paths past the Windows 260-character
        // limit; the per-key gate already serializes builds of one key, so 48 random bits never collide in practice.
        var buildId = Guid.NewGuid().ToString(format: "N")[..12];
        var buildRoot = Path.Combine(
            path1: m_cacheDirectory,
            path2: (".build-" + buildId)
        );
        var snapshotBase = SnapshotBase(paths: descriptor.Stages.Select(selector: static stage => stage.Path).Concat(second: closure.Contents.Keys));

        Directory.CreateDirectory(path: buildRoot);
        try {
            foreach (var dependency in closure.Contents) {
                var dependencyPath = SnapshotPath(
                    root: buildRoot,
                    snapshotBase: snapshotBase,
                    path: dependency.Key
                );

                Directory.CreateDirectory(path: Path.GetDirectoryName(path: dependencyPath)!);
                await File.WriteAllTextAsync(
                    dependencyPath,
                    dependency.Value,
                    Encoding.UTF8,
                    cancellationToken
                ).ConfigureAwait(continueOnCapturedContext: false);
            }

            foreach (var stage in descriptor.Stages) {
                cancellationToken.ThrowIfCancellationRequested();
                var snapshotPath = SnapshotPath(
                    root: buildRoot,
                    snapshotBase: snapshotBase,
                    path: stage.Path
                );

                Directory.CreateDirectory(path: Path.GetDirectoryName(path: snapshotPath)!);
                await File.WriteAllTextAsync(
                    snapshotPath,
                    stage.Source,
                    Encoding.UTF8,
                    cancellationToken
                ).ConfigureAwait(continueOnCapturedContext: false);

                var stageStem = Path.Combine(
                    path1: buildRoot,
                    path2: StageName(stage: stage.Stage)
                );
                var spvPath = $"{stageStem}.spv.tmp";
                var dxilPath = $"{stageStem}.dxil.tmp";
                var steps = identity.Stages.First(predicate: compiled => (compiled.Stage == stage.Stage)).Steps;
                var stageDiagnostics = new List<ShaderDiagnostic>();
                var includeDirectory = Path.GetDirectoryName(path: snapshotPath);
                var diagnosticPaths = new Dictionary<string, string>(comparer: StringComparer.OrdinalIgnoreCase) { [snapshotPath] = Path.GetFullPath(path: stage.Path) };

                foreach (var dependency in closure.Contents.Keys) {
                    diagnosticPaths[SnapshotPath(
                        path: dependency,
                        root: buildRoot,
                        snapshotBase: snapshotBase
                    )] = dependency;
                }

                var spv = await RunStepAsync(
                    cancellationToken: cancellationToken,
                    includeDirectory: includeDirectory,
                    input: snapshotPath,
                    output: spvPath,
                    step: steps[0]
                ).ConfigureAwait(continueOnCapturedContext: false);

                stageDiagnostics.AddRange(collection: ParseDxcDiagnostics(
                    paths: diagnosticPaths,
                    stage: stage,
                    text: ((spv.Stdout + "\n") + spv.Stderr)
                ));
                if (
                    (spv.ExitCode == 0) &&
                    File.Exists(path: spvPath)
                ) { spirv[stage.Stage] = File.ReadAllBytes(path: spvPath); } else {
                    AddToolFailure(
                    stageDiagnostics,
                    "dxc (SPIR-V)",
                    spv.ExitCode,
                    ((spv.Stdout + "\n") + spv.Stderr)
                );
                }

                var dx = await RunStepAsync(
                    cancellationToken: cancellationToken,
                    includeDirectory: includeDirectory,
                    input: snapshotPath,
                    output: dxilPath,
                    step: steps[1]
                ).ConfigureAwait(continueOnCapturedContext: false);

                stageDiagnostics.AddRange(collection: ParseDxcDiagnostics(
                    paths: diagnosticPaths,
                    stage: stage,
                    text: ((dx.Stdout + "\n") + dx.Stderr)
                ));
                if (
                    (dx.ExitCode == 0) &&
                    File.Exists(path: dxilPath)
                ) { dxil[stage.Stage] = File.ReadAllBytes(path: dxilPath); } else {
                    AddToolFailure(
                    stageDiagnostics,
                    "dxc (DXIL)",
                    dx.ExitCode,
                    ((dx.Stdout + "\n") + dx.Stderr)
                );
                }

                diagnostics.AddRange(collection: stageDiagnostics);
                if (stageDiagnostics.Any(predicate: static diagnostic => diagnostic.IsError)) {
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

            foreach (var stage in descriptor.Stages) {
                var stageStem = Path.Combine(
                    path1: buildRoot,
                    path2: StageName(stage: stage.Stage)
                );

                PublishOnce(
                    destination: BytecodePath(
                        stem,
                        stage.Stage,
                        "spv"
                    ),
                    temporary: $"{stageStem}.spv.tmp"
                );
                PublishOnce(
                    destination: BytecodePath(
                        stem,
                        stage.Stage,
                        "dxil"
                    ),
                    temporary: $"{stageStem}.dxil.tmp"
                );
            }
            var completeTemp = Path.Combine(
                path1: buildRoot,
                path2: "complete.tmp"
            );

            // The marker goes last: every bytecode file it vouches for is already in place, published by this build
            // or by a peer that got there first.
            await File.WriteAllTextAsync(
                cancellationToken: cancellationToken,
                contents: sourceHash,
                path: completeTemp
            ).ConfigureAwait(continueOnCapturedContext: false);
            PublishOnce(
                destination: completePath,
                temporary: completeTemp
            );
            return new CompiledShader(
                descriptor.Name,
                FirstPath(descriptor: descriptor),
                sourceHash,
                spirv,
                dxil,
                diagnostics,
                identity
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
    /// <summary>Moves a finished file to its cache name unless a peer already published that name.</summary>
    /// <remarks>Cache names are addressed by content, so a name that already exists holds what this build would have
    /// written, and the first publisher wins. Replacing it instead races any compiler, in this process or another
    /// sharing the directory, that is reading or publishing the same name, and the operating system refuses the
    /// replacement. The unpublished temporary goes with the build directory.</remarks>
    private static void PublishOnce(string temporary, string destination) {
        try {
            File.Move(
                destFileName: destination,
                overwrite: false,
                sourceFileName: temporary
            );
        } catch (IOException) when (File.Exists(path: destination)) { }
    }
    private async Task<ChildProcessResult> RunToolAsync(string name, IReadOnlyList<string> args, CancellationToken cancellationToken) {
        var executable = ((m_toolchain.Directory is null)
            ? name
            : m_toolchain.Resolve(name: name)
        );

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
    private static string BytecodePath(string stem, ShaderStage stage, string target) => $"{stem}.{StageName(stage: stage)}.{target}";
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
}
