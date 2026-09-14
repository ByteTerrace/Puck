using Puck.Abstractions.Gpu;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Puck.Shaders;

/// <summary>
/// Compiles native HLSL and GLSL stages, including Shadertoy GLSL through the explicit
/// adapter, producing SPIR-V and DXIL candidates for both graphics and compute.
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

    /// <summary>Compiles one Shadertoy source as a compute stage.</summary>
    public CompiledShader Compile(string name, string sourcePath, string sourceText) =>
        CompileAsync(
            name,
            sourcePath,
            sourceText
        ).GetAwaiter().GetResult();
    public Task<CompiledShader> CompileAsync(string name, string sourcePath, string sourceText, CancellationToken cancellationToken = default) =>
        CompileAsync(
            ShaderCompilationRequest.Compute(
                name,
                sourcePath,
                sourceText
            ),
            cancellationToken
        );
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
        var collected = CollectDependencies(descriptor: descriptor);
        var stagePaths = descriptor.Stages.Select(selector: static stage => Path.GetFullPath(path: stage.Path)).ToHashSet(comparer: StringComparer.OrdinalIgnoreCase);

        if (collected.Dependencies.Any(predicate: dependency => stagePaths.Contains(item: Path.GetFullPath(path: dependency.Path)))) {
            throw new InvalidDataException(message: "A shader stage source cannot also be an include dependency; source and include snapshots would overlap.");
        }
        var cacheKey = CreateCacheKey(
            descriptor,
            collected.Dependencies
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
            collected: collected,
            descriptor: descriptor
        ).ConfigureAwait(continueOnCapturedContext: false);
        } finally { gate.Release(); }
    }
    private async Task<CompiledShader> CompileCoreLockedAsync(
        ShaderCompilationRequest descriptor,
        CollectedDependencies collected,
        string cacheKey,
        CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        var stem = Path.Combine(
            path1: m_cacheDirectory,
            path2: cacheKey
        );
        var sourceHash = Hash(value: SerializeDescriptor(descriptor: descriptor));
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
            cachedSpirv[stage.Stage] = File.ReadAllBytes(path: spirvPath);
            cachedDxil[stage.Stage] = File.ReadAllBytes(path: dxilPath);
        }
        if (allCached) {
            return new CompiledShader(
                descriptor.Name,
                FirstPath(descriptor: descriptor),
                sourceHash,
                cachedSpirv,
                cachedDxil,
                [],
                collected.Dependencies
            );
        }

        var diagnostics = new List<ShaderDiagnostic>();
        var spirv = new Dictionary<ShaderStage, ReadOnlyMemory<byte>>();
        var dxil = new Dictionary<ShaderStage, ReadOnlyMemory<byte>>();
        var buildId = Guid.NewGuid().ToString(format: "N");
        var buildRoot = Path.Combine(
            path1: m_cacheDirectory,
            path2: (".build-" + buildId)
        );

        Directory.CreateDirectory(path: buildRoot);
        try {
            foreach (var dependency in collected.Contents) {
                var dependencyPath = SnapshotPath(
                    buildRoot,
                    dependency.Key
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
                ShadertoyShaderAdapter.AdaptedSource adapted;

                try {
                    adapted = Prepare(
                    descriptor: descriptor,
                    stage: stage
                );
                } catch (InvalidDataException exception) {
                    diagnostics.Add(item: new ShaderDiagnostic(
                        0,
                        0,
                        exception.Message,
                        true,
                        stage.Stage,
                        Path.GetFullPath(path: stage.Path)
                    ));
                    return new CompiledShader(
                        descriptor.Name,
                        FirstPath(descriptor: descriptor),
                        sourceHash,
                        spirv,
                        dxil,
                        diagnostics,
                        collected.Dependencies
                    );
                }
                var snapshotPath = SnapshotPath(
                    buildRoot,
                    stage.Path
                );

                Directory.CreateDirectory(path: Path.GetDirectoryName(path: snapshotPath)!);
                await File.WriteAllTextAsync(
                    snapshotPath,
                    adapted.Text,
                    Encoding.UTF8,
                    cancellationToken
                ).ConfigureAwait(continueOnCapturedContext: false);

                var displaySourcePath = Path.Combine(
                    path1: m_cacheDirectory,
                    path2: $"{cacheKey}.{StageName(stage: stage.Stage)}.source"
                );
                var displaySourceTemp = Path.Combine(
                    path1: buildRoot,
                    path2: $"{cacheKey}.{buildId}.{StageName(stage: stage.Stage)}.source.tmp"
                );

                await File.WriteAllTextAsync(
                    displaySourceTemp,
                    adapted.Text,
                    Encoding.UTF8,
                    cancellationToken
                ).ConfigureAwait(continueOnCapturedContext: false);
                File.Move(
                    destFileName: displaySourcePath,
                    overwrite: true,
                    sourceFileName: displaySourceTemp
                );
                var stageStem = Path.Combine(
                    path1: buildRoot,
                    path2: $"{cacheKey}.{buildId}.{StageName(stage: stage.Stage)}"
                );
                var spvPath = $"{stageStem}.spv.tmp";
                var dxilPath = $"{stageStem}.dxil.tmp";
                var profile = Profile(stage: stage.Stage);
                var stageDiagnostics = new List<ShaderDiagnostic>();
                var includeDirectory = Path.GetDirectoryName(path: snapshotPath);
                var diagnosticPaths = new Dictionary<string, string>(comparer: StringComparer.OrdinalIgnoreCase) { [snapshotPath] = Path.GetFullPath(path: stage.Path) };

                foreach (var dependency in collected.Contents.Keys) {
                    diagnosticPaths[SnapshotPath(
                        path: dependency,
                        root: buildRoot
                    )] = dependency;
                }

                if (stage.Language == ShaderSourceLanguage.Hlsl) {
                    var spv = await RunDxcAsync(
                        snapshotPath,
                        spvPath,
                        profile,
                        stage.EntryPoint,
                        spirv: true,
                        includeDirectory,
                        cancellationToken
                    ).ConfigureAwait(continueOnCapturedContext: false);

                    stageDiagnostics.AddRange(collection: ParseDxcDiagnostics(
                        ((spv.Stdout + "\n") + spv.Stderr),
                        stage,
                        0,
                        diagnosticPaths
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

                    var dx = await RunDxcAsync(
                        snapshotPath,
                        dxilPath,
                        profile,
                        stage.EntryPoint,
                        spirv: false,
                        includeDirectory,
                        cancellationToken
                    ).ConfigureAwait(continueOnCapturedContext: false);

                    stageDiagnostics.AddRange(collection: ParseDxcDiagnostics(
                        ((dx.Stdout + "\n") + dx.Stderr),
                        stage,
                        0,
                        diagnosticPaths
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
                } else {
                    var glsl = await RunGlslAsync(
                        cancellationToken: cancellationToken,
                        includeDirectory: includeDirectory,
                        output: spvPath,
                        source: snapshotPath,
                        stage: stage
                    ).ConfigureAwait(continueOnCapturedContext: false);

                    stageDiagnostics.AddRange(collection: ParseGlslDiagnostics(
                        ((glsl.Stdout + "\n") + glsl.Stderr),
                        stage,
                        adapted.PrefixLineCount,
                        diagnosticPaths
                    ));
                    if (
                        (glsl.ExitCode == 0) &&
                        File.Exists(path: spvPath)
                    ) { spirv[stage.Stage] = File.ReadAllBytes(path: spvPath); } else {
                        AddToolFailure(
                        stageDiagnostics,
                        "glslang",
                        glsl.ExitCode,
                        ((glsl.Stdout + "\n") + glsl.Stderr)
                    );
                    }

                    if (spirv.ContainsKey(key: stage.Stage)) {
                        var hlslPath = $"{stageStem}.translated.hlsl";
                        var cross = await RunToolAsync(
                            args: ["--hlsl", "--shader-model", "66", spvPath, "--output", hlslPath],
                            cancellationToken: cancellationToken,
                            fallback: null,
                            name: "spirv-cross"
                        ).ConfigureAwait(continueOnCapturedContext: false);

                        if (
                            (cross.ExitCode != 0) ||
                            !File.Exists(path: hlslPath)
                        ) {
                            AddToolFailure(
                                stageDiagnostics,
                                "spirv-cross",
                                cross.ExitCode,
                                ((cross.Stdout + "\n") + cross.Stderr)
                            );
                        } else {
                            // SPIRV-Cross preserves GLSL descriptor numbers in HLSL register annotations (for
                            // example, Vulkan binding 1 becomes t1/s1). The Direct3D backend packs sampled-image
                            // bindings densely in descriptor-list order (t0/s0, t1/s1, ...), while retaining every
                            // declared channel slot even when optimization removes an unused sampler. Normalize the
                            // translated Shadertoy input declarations to that shared ABI before DXC sees them.
                            var translated = File.ReadAllText(path: hlslPath);
                            var remapped = RemapTranslatedHlslRegisters(
                                request: descriptor,
                                translated: translated
                            );

                            if (!string.Equals(
                                a: translated,
                                b: remapped,
                                comparisonType: StringComparison.Ordinal
                            )) {
                                await File.WriteAllTextAsync(
                                    hlslPath,
                                    remapped,
                                    Encoding.UTF8,
                                    cancellationToken
                                ).ConfigureAwait(continueOnCapturedContext: false);
                            }
                            var dx = await RunDxcAsync(
                                hlslPath,
                                dxilPath,
                                profile,
                                "main",
                                spirv: false,
                                includeDirectory,
                                cancellationToken
                            ).ConfigureAwait(continueOnCapturedContext: false);

                            stageDiagnostics.AddRange(collection: ParseDxcDiagnostics(
                                ((dx.Stdout + "\n") + dx.Stderr),
                                stage,
                                0,
                                diagnosticPaths
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
                        }
                    }
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
                        collected.Dependencies
                    );
                }
            }

            foreach (var stage in descriptor.Stages) {
                var stageStem = Path.Combine(
                    path1: buildRoot,
                    path2: $"{cacheKey}.{buildId}.{StageName(stage: stage.Stage)}"
                );

                File.Move(
                    $"{stageStem}.spv.tmp",
                    BytecodePath(
                        stem,
                        stage.Stage,
                        "spv"
                    ),
                    true
                );
                File.Move(
                    $"{stageStem}.dxil.tmp",
                    BytecodePath(
                        stem,
                        stage.Stage,
                        "dxil"
                    ),
                    true
                );
            }
            var completeTemp = $"{completePath}.{buildId}.tmp";

            await File.WriteAllTextAsync(
                cancellationToken: cancellationToken,
                contents: sourceHash,
                path: completeTemp
            ).ConfigureAwait(continueOnCapturedContext: false);
            File.Move(
                destFileName: completePath,
                overwrite: true,
                sourceFileName: completeTemp
            );
            return new CompiledShader(
                descriptor.Name,
                FirstPath(descriptor: descriptor),
                sourceHash,
                spirv,
                dxil,
                diagnostics,
                collected.Dependencies
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
    private async Task<ShaderProcessResult> RunDxcAsync(string source, string output, string profile, string entry,
        bool spirv, string? includeDirectory, CancellationToken cancellationToken) {
        var args = new List<string>();

        if (spirv) { args.AddRange(collection: ["-spirv", "-fspv-target-env=vulkan1.3", "-fspv-entrypoint-name=main"]); }
        if (!string.IsNullOrWhiteSpace(value: includeDirectory)) { args.Add(item: ("-I" + includeDirectory)); }
        args.AddRange(collection: ["-T", profile, "-E", entry, "-Fo", output, source]);
        return await RunToolAsync(
            args: args,
            cancellationToken: cancellationToken,
            fallback: null,
            name: "dxc"
        ).ConfigureAwait(continueOnCapturedContext: false);
    }
    private Task<ShaderProcessResult> RunGlslAsync(string source, string output, ShaderStageSource stage, string? includeDirectory, CancellationToken cancellationToken) {
        var args = new List<string> { "-V", "-S", StageName(stage: stage.Stage), "-e", ((stage.Language == ShaderSourceLanguage.ShadertoyGlsl)
            ? "main"
            : stage.EntryPoint), "-o", output };

        if (!string.IsNullOrWhiteSpace(value: includeDirectory)) { args.Add(item: ("-I" + includeDirectory)); }
        args.Add(item: source);
        return RunToolAsync(
            args: args,
            cancellationToken: cancellationToken,
            fallback: "glslang",
            name: "glslangValidator"
        );
    }
    private async Task<ShaderProcessResult> RunToolAsync(string name, string? fallback, IReadOnlyList<string> args, CancellationToken cancellationToken) {
        var executable = ((m_toolchain.Directory is null)
            ? name
            : m_toolchain.Resolve(
                fallbackName: fallback,
                name: name
            )
        );

        try {
            return await m_processRunner.RunAsync(
            arguments: args,
            cancellationToken: cancellationToken,
            fileName: executable
        ).ConfigureAwait(continueOnCapturedContext: false);
        } catch (System.ComponentModel.Win32Exception) when (((m_toolchain.Directory is null) && (fallback is not null))) {
            try {
                return await m_processRunner.RunAsync(
                arguments: args,
                cancellationToken: cancellationToken,
                fileName: fallback
            ).ConfigureAwait(continueOnCapturedContext: false);
            } catch (System.ComponentModel.Win32Exception) {
                throw new ShaderToolMissingException(
                directory: null,
                tool: $"{name} (or {fallback})"
            );
            }
        } catch (System.ComponentModel.Win32Exception) {
            throw new ShaderToolMissingException(
            name,
            m_toolchain.Directory
        );
        }
    }

    internal static string RemapTranslatedHlslRegisters(string translated, IReadOnlyDictionary<string, uint> channels) {
        ArgumentNullException.ThrowIfNull(channels);
        var descriptors = OrderedChannels(channels: channels)
            .Select(selector: pair => new ShaderDescriptorBinding(
            pair.Value,
            GpuComputeBindingKind.SampledImage
        ))
            .ToArray();

        return RemapTranslatedHlslRegisters(
            descriptors: descriptors,
            translated: translated
        );
    }

    private static string RemapTranslatedHlslRegisters(string translated, ShaderCompilationRequest request) =>
        ((request.DescriptorBindings.Count == 0)
            ? RemapTranslatedHlslRegisters(
                translated,
                request.Channels
            )
            : RemapTranslatedHlslRegisters(
                translated,
                request.DescriptorBindings
            )
        );

    internal static string RemapTranslatedHlslRegisters(string translated, IReadOnlyList<ShaderDescriptorBinding> descriptors) {
        ArgumentNullException.ThrowIfNull(translated);
        ArgumentNullException.ThrowIfNull(descriptors);
        if (descriptors.Count == 0) { return translated; }

        var logicalToDenseSrv = new Dictionary<uint, uint>();
        var logicalToDenseUav = new Dictionary<uint, uint>();
        var nextSrv = 0U;
        var nextUav = 0U;

        foreach (var descriptor in descriptors) {
            var destination = (IsSrv(kind: descriptor.Kind)
                ? logicalToDenseSrv
                : logicalToDenseUav
            );
            var next = (IsSrv(kind: descriptor.Kind)
                ? nextSrv
                : nextUav
            );

            destination.TryAdd(
                key: descriptor.VulkanBinding,
                value: next
            );
            var increment = descriptor.Count;

            if (IsSrv(kind: descriptor.Kind)) { nextSrv = checked((nextSrv + increment)); } else { nextUav = checked((nextUav + increment)); }
        }

        return HlslRegisterPattern().Replace(
            translated,
            match => {
                var kind = match.Groups[1].Value;

                if (!uint.TryParse(
                    match.Groups[2].Value,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var logical
                )) {
                    return match.Value;
                }
                var map = ((kind is "t" or "s")
                    ? logicalToDenseSrv
                    : logicalToDenseUav
                );

                return (map.TryGetValue(
                    key: logical,
                    value: out var dense
                )
                    ? $"register({kind}{dense}, space0)"
                    : match.Value
                );
            }
        );
    }

    private static bool IsSrv(GpuComputeBindingKind kind) => (kind is
        GpuComputeBindingKind.SampledImage or
        GpuComputeBindingKind.StorageBufferRead or
        GpuComputeBindingKind.AccelerationStructure);
    private static IEnumerable<KeyValuePair<string, uint>> OrderedChannels(IReadOnlyDictionary<string, uint> channels) =>
        channels.OrderBy(keySelector: static pair => (ChannelOrdinal(name: pair.Key).HasValue
            ? 0
            : 1))
            .ThenBy(keySelector: static pair => (ChannelOrdinal(name: pair.Key) ?? uint.MaxValue))
            .ThenBy(
            static pair => pair.Key,
            StringComparer.Ordinal
        );
    private static uint? ChannelOrdinal(string name) {
        const string Prefix = "iChannel";

        if (
            !name.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: Prefix
        ) ||
            !uint.TryParse(
            name.AsSpan(start: Prefix.Length),
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var ordinal
        )
        ) {
            return null;
        }
        return ordinal;
    }
    [GeneratedRegex(@"register\(\s*([tsu])(\d+)(?:\s*,\s*space\d+)?\s*\)")]
    private static partial Regex HlslRegisterPattern();
    private static ShadertoyShaderAdapter.AdaptedSource Prepare(ShaderStageSource stage, ShaderCompilationRequest descriptor) {
        if (stage.Language == ShaderSourceLanguage.ShadertoyGlsl) {
            return ShadertoyShaderAdapter.Adapt(
                stage.Source,
                descriptor.Channels,
                stage.GroupSizeX,
                stage.GroupSizeY,
                stage.GroupSizeZ,
                descriptor.OutputFormat,
                descriptor.Config
            );
        }
        return new ShadertoyShaderAdapter.AdaptedSource(
            stage.Source,
            0,
            []
        );
    }

    private sealed record CollectedDependencies(
        IReadOnlyList<ShaderSourceDependency> Dependencies,
        IReadOnlyDictionary<string, string> Contents
    );

    private CollectedDependencies CollectDependencies(ShaderCompilationRequest descriptor) {
        var result = new List<ShaderSourceDependency>();
        var contents = new Dictionary<string, string>(comparer: StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);

        foreach (var stage in descriptor.Stages) {
            CollectIncludes(
                stage.Path,
                stage.Source,
                visited,
                result,
                contents
            );
        }
        return new CollectedDependencies(
            Contents: contents,
            Dependencies: result
        );
    }
    private static void CollectIncludes(
        string path,
        string source,
        HashSet<string> visited,
        List<ShaderSourceDependency> result,
        Dictionary<string, string> contents) {
        foreach (Match match in IncludePattern().Matches(input: source)) {
            var includePath = match.Groups[1].Value;
            var fullPath = Path.GetFullPath(path: Path.Combine(
                path1: (Path.GetDirectoryName(path: Path.GetFullPath(path: path)) ?? Environment.CurrentDirectory),
                path2: includePath
            ));

            if (
                !visited.Add(item: fullPath) ||
                !File.Exists(path: fullPath)
            ) {
                continue;
            }
            var included = File.ReadAllText(path: fullPath);

            contents[fullPath] = included;
            result.Add(item: new ShaderSourceDependency(
                fullPath,
                Hash(value: included)
            ));
            CollectIncludes(
                contents: contents,
                path: fullPath,
                result: result,
                source: included,
                visited: visited
            );
        }
    }
    private static string SnapshotPath(string root, string path) {
        var fullPath = Path.GetFullPath(path: path);
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
        var volumeKey = Hash(value: volume.ToUpperInvariant())[..16];

        return Path.Combine(
            path1: root,
            path2: ("volume-" + volumeKey),
            path3: relative
        );
    }
    private string CreateCacheKey(ShaderCompilationRequest descriptor, IReadOnlyList<ShaderSourceDependency> dependencies) {
        var builder = new StringBuilder(value: CompilerVersion).Append(value: '|').Append(value: SerializeDescriptor(descriptor: descriptor)).Append(value: '|').Append(value: ShadertoyShaderAdapter.Version)
            .Append(value: '|').Append(value: m_toolchain.Identity);

        foreach (var stage in descriptor.Stages) {
            builder.Append(value: '|').Append(value: stage.Stage).Append(value: '|').Append(value: Profile(stage: stage.Stage)).Append(value: '|').Append(value: stage.EntryPoint)
                .Append(value: "|-spirv|-fspv-target-env=vulkan1.3|-fspv-entrypoint-name=main|-T|").Append(value: Profile(stage: stage.Stage)).Append(value: "|-E|").Append(value: stage.EntryPoint)
                .Append(value: "|-Fo|").Append(value: StageName(stage: stage.Stage)).Append(value: ".target|glslangValidator|-V|-S|").Append(value: StageName(stage: stage.Stage))
                .Append(value: "|-e|").Append(value: stage.EntryPoint).Append(value: "|spirv-cross|--hlsl|--shader-model|66");
        }
        foreach (var dependency in dependencies.OrderBy(
            static dependency => dependency.Path,
            StringComparer.OrdinalIgnoreCase
        )) {
            builder.Append(value: '|').Append(value: dependency.Path).Append(value: '|').Append(value: dependency.ContentHash);
        }
        return Hash(value: builder.ToString());
    }
    private static string SerializeDescriptor(ShaderCompilationRequest descriptor) =>
        (((string.Join(
            separator: "\n",
            values: descriptor.Stages.Select(selector: stage => $"{stage.Stage}|{stage.Path}|{stage.Language}|{stage.EntryPoint}|{stage.GroupSizeX},{stage.GroupSizeY},{stage.GroupSizeZ}|{stage.Source}")
        ) +
        string.Join(
            separator: "\n",
            values: descriptor.Channels.OrderBy(
                static pair => pair.Key,
                StringComparer.Ordinal
            ).Select(selector: static pair => $"channel|{pair.Key}|{pair.Value}")
        )) +
        $"\nformat|{descriptor.OutputFormat}|config|{string.Join(
            separator: ",",
            values: descriptor.Config.OrderBy(
                static pair => pair.Key,
                StringComparer.Ordinal
            ).Select(selector: static pair => $"{pair.Key}:{pair.Value.Type}")
        )}") +
        $"\ndescriptors|{string.Join(
            separator: ",",
            values: descriptor.DescriptorBindings.Select(selector: static binding => $"{binding.VulkanBinding}:{binding.Kind}:{binding.Count}")
        )}");
    private static string Hash(string value) => Convert.ToHexStringLower(inArray: SHA256.HashData(source: Encoding.UTF8.GetBytes(s: value)));
    private static string FirstPath(ShaderCompilationRequest descriptor) => descriptor.Stages[0].Path;
    private static string BytecodePath(string stem, ShaderStage stage, string target) => $"{stem}.{StageName(stage: stage)}.{target}";
    private static string StageName(ShaderStage stage) => stage switch { ShaderStage.Vertex => "vert", ShaderStage.Fragment => "frag", _ => "comp" };
    private static string Profile(ShaderStage stage) => stage switch { ShaderStage.Vertex => "vs_6_6", ShaderStage.Fragment => "ps_6_6", _ => "cs_6_6" };
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
    private static List<ShaderDiagnostic> ParseGlslDiagnostics(
        string text,
        ShaderStageSource stage,
        int prefix,
        IReadOnlyDictionary<string, string> paths) {
        var result = new List<ShaderDiagnostic>();

        foreach (var line in text.Split('\n')) {
            var match = GlslDiagnosticPattern().Match(input: line.TrimEnd(trimChar: '\r'));

            if (
                match.Success &&
                int.TryParse(
                match.Groups[3].Value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var lineNumber
            )
            ) {
                var path = DiagnosticPath(
                    match.Groups[2].Value,
                    stage,
                    paths
                );
                var authoredLine = string.Equals(
                    a: Path.GetFullPath(path: path),
                    b: Path.GetFullPath(path: stage.Path),
                    comparisonType: StringComparison.OrdinalIgnoreCase
                );

                result.Add(item: new ShaderDiagnostic(
                    (authoredLine
                    ? Math.Max(
                            val1: 0,
                            val2: (lineNumber - prefix)
                        )
                    : lineNumber),
                    0,
                    match.Groups[4].Value.Trim(),
                    match.Groups[1].Value.Equals(
                        comparisonType: StringComparison.OrdinalIgnoreCase,
                        value: "ERROR"
                    ),
                    stage.Stage,
                    path
                ));
            }
        }
        return result;
    }
    private static List<ShaderDiagnostic> ParseDxcDiagnostics(
        string text,
        ShaderStageSource stage,
        int prefix,
        IReadOnlyDictionary<string, string> paths) {
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
                var path = DiagnosticPath(
                    match.Groups[1].Value,
                    stage,
                    paths
                );
                var authoredLine = string.Equals(
                    a: Path.GetFullPath(path: path),
                    b: Path.GetFullPath(path: stage.Path),
                    comparisonType: StringComparison.OrdinalIgnoreCase
                );

                result.Add(item: new ShaderDiagnostic(
                    (authoredLine
                    ? Math.Max(
                            val1: 0,
                            val2: (lineNumber - prefix)
                        )
                    : lineNumber),
                    int.Parse(
                        match.Groups[3].Value,
                        CultureInfo.InvariantCulture
                    ),
                    match.Groups[5].Value.Trim(),
                    (match.Groups[4].Value == "error"),
                    stage.Stage,
                    path
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
    [GeneratedRegex(@"^(ERROR|WARNING):\s*(.*):(\d+):\s*(.*)$")]
    private static partial Regex GlslDiagnosticPattern();
    [GeneratedRegex(@"^(.*):(\d+):(\d+):\s*(error|warning):\s*(.*)$")]
    private static partial Regex DxcDiagnosticPattern();
    [GeneratedRegex(@"^\s*#include\s*[<""]([^>""]+)[>""]")]
    private static partial Regex IncludePattern();
}
