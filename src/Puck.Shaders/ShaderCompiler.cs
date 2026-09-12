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
public sealed partial class ShaderCompiler
{
    private const string CompilerVersion = "puck-shader-compiler-4";
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> m_gates = new(StringComparer.Ordinal);
    private readonly string m_cacheDirectory;
    private readonly IShaderProcessRunner m_processRunner;
    private readonly ShaderToolchain m_toolchain;

    public ShaderCompiler(string cacheDirectory, string? toolchainDirectory = null)
        : this(cacheDirectory, new ShaderProcessRunner(), toolchainDirectory) { }

    internal ShaderCompiler(string cacheDirectory, IShaderProcessRunner processRunner, string? toolchainDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        ArgumentNullException.ThrowIfNull(processRunner);
        m_cacheDirectory = Path.GetFullPath(cacheDirectory);
        Directory.CreateDirectory(m_cacheDirectory);
        m_processRunner = processRunner;
        m_toolchain = new ShaderToolchain(toolchainDirectory);
    }

    /// <summary>Gets the cache directory used by this compiler.</summary>
    public string CacheDirectory => m_cacheDirectory;
    /// <summary>Gets the resolved toolchain configuration.</summary>
    public ShaderToolchain Toolchain => m_toolchain;

    /// <summary>Compiles one Shadertoy source as a compute stage.</summary>
    public CompiledShader Compile(string name, string sourcePath, string sourceText) =>
        CompileAsync(name, sourcePath, sourceText).GetAwaiter().GetResult();

    public Task<CompiledShader> CompileAsync(string name, string sourcePath, string sourceText, CancellationToken cancellationToken = default) =>
        CompileAsync(ShaderCompilationRequest.Compute(name, sourcePath, sourceText), cancellationToken);

    public CompiledShader Compile(ShaderCompilationRequest descriptor) =>
        CompileAsync(descriptor).GetAwaiter().GetResult();

    /// <summary>Compiles every stage in a descriptor away from the presentation thread.</summary>
    public Task<CompiledShader> CompileAsync(ShaderCompilationRequest descriptor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return Task.Run(() => CompileCoreAsync(descriptor, cancellationToken), cancellationToken);
    }

    private async Task<CompiledShader> CompileCoreAsync(ShaderCompilationRequest descriptor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var collected = CollectDependencies(descriptor);
        var stagePaths = descriptor.Stages.Select(static stage => Path.GetFullPath(stage.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (collected.Dependencies.Any(dependency => stagePaths.Contains(Path.GetFullPath(dependency.Path)))) {
            throw new InvalidDataException("A shader stage source cannot also be an include dependency; source and include snapshots would overlap.");
        }
        var cacheKey = CreateCacheKey(descriptor, collected.Dependencies);
        var gate = m_gates.GetOrAdd(cacheKey, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await CompileCoreLockedAsync(descriptor, collected, cacheKey, cancellationToken).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private async Task<CompiledShader> CompileCoreLockedAsync(
        ShaderCompilationRequest descriptor,
        CollectedDependencies collected,
        string cacheKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stem = Path.Combine(m_cacheDirectory, cacheKey);
        var sourceHash = Hash(SerializeDescriptor(descriptor));
        var completePath = stem + ".complete";
        var cachedSpirv = new Dictionary<ShaderStage, ReadOnlyMemory<byte>>();
        var cachedDxil = new Dictionary<ShaderStage, ReadOnlyMemory<byte>>();
        var allCached = File.Exists(completePath);
        foreach (var stage in descriptor.Stages)
        {
            var spirvPath = BytecodePath(stem, stage.Stage, "spv");
            var dxilPath = BytecodePath(stem, stage.Stage, "dxil");
            if (!allCached || !File.Exists(spirvPath) || !File.Exists(dxilPath)) { allCached = false; break; }
            cachedSpirv[stage.Stage] = File.ReadAllBytes(spirvPath);
            cachedDxil[stage.Stage] = File.ReadAllBytes(dxilPath);
        }
        if (allCached)
        {
            return new CompiledShader(descriptor.Name, FirstPath(descriptor), sourceHash,
                cachedSpirv, cachedDxil, [], collected.Dependencies);
        }

        var diagnostics = new List<ShaderDiagnostic>();
        var spirv = new Dictionary<ShaderStage, ReadOnlyMemory<byte>>();
        var dxil = new Dictionary<ShaderStage, ReadOnlyMemory<byte>>();
        var buildId = Guid.NewGuid().ToString("N");
        var buildRoot = Path.Combine(m_cacheDirectory, ".build-" + buildId);
        Directory.CreateDirectory(buildRoot);
        try
        {
            foreach (var dependency in collected.Contents)
            {
                var dependencyPath = SnapshotPath(buildRoot, dependency.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(dependencyPath)!);
                await File.WriteAllTextAsync(dependencyPath, dependency.Value, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            }

            foreach (var stage in descriptor.Stages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ShadertoyShaderAdapter.AdaptedSource adapted;
                try { adapted = Prepare(stage, descriptor); }
                catch (InvalidDataException exception)
                {
                    diagnostics.Add(new ShaderDiagnostic(0, 0, exception.Message, true, stage.Stage, Path.GetFullPath(stage.Path)));
                    return new CompiledShader(descriptor.Name, FirstPath(descriptor), sourceHash, spirv, dxil, diagnostics, collected.Dependencies);
                }
                var snapshotPath = SnapshotPath(buildRoot, stage.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
                await File.WriteAllTextAsync(snapshotPath, adapted.Text, Encoding.UTF8, cancellationToken).ConfigureAwait(false);

                var displaySourcePath = Path.Combine(m_cacheDirectory, $"{cacheKey}.{StageName(stage.Stage)}.source");
                var displaySourceTemp = Path.Combine(buildRoot, $"{cacheKey}.{buildId}.{StageName(stage.Stage)}.source.tmp");
                await File.WriteAllTextAsync(displaySourceTemp, adapted.Text, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                File.Move(displaySourceTemp, displaySourcePath, true);
                var stageStem = Path.Combine(buildRoot, $"{cacheKey}.{buildId}.{StageName(stage.Stage)}");
                var spvPath = $"{stageStem}.spv.tmp";
                var dxilPath = $"{stageStem}.dxil.tmp";
                var profile = Profile(stage.Stage);
                var stageDiagnostics = new List<ShaderDiagnostic>();
                var includeDirectory = Path.GetDirectoryName(snapshotPath);
                var diagnosticPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [snapshotPath] = Path.GetFullPath(stage.Path) };
                foreach (var dependency in collected.Contents.Keys)
                {
                    diagnosticPaths[SnapshotPath(buildRoot, dependency)] = dependency;
                }

                if (stage.Language == ShaderSourceLanguage.Hlsl)
                {
                    var spv = await RunDxcAsync(snapshotPath, spvPath, profile, stage.EntryPoint, spirv: true, includeDirectory, cancellationToken).ConfigureAwait(false);
                    stageDiagnostics.AddRange(ParseDxcDiagnostics(spv.Stdout + "\n" + spv.Stderr, stage, 0, diagnosticPaths));
                    if (spv.ExitCode == 0 && File.Exists(spvPath)) { spirv[stage.Stage] = File.ReadAllBytes(spvPath); }
                    else { AddToolFailure(stageDiagnostics, "dxc (SPIR-V)", spv.ExitCode, spv.Stdout + "\n" + spv.Stderr); }

                    var dx = await RunDxcAsync(snapshotPath, dxilPath, profile, stage.EntryPoint, spirv: false, includeDirectory, cancellationToken).ConfigureAwait(false);
                    stageDiagnostics.AddRange(ParseDxcDiagnostics(dx.Stdout + "\n" + dx.Stderr, stage, 0, diagnosticPaths));
                    if (dx.ExitCode == 0 && File.Exists(dxilPath)) { dxil[stage.Stage] = File.ReadAllBytes(dxilPath); }
                    else { AddToolFailure(stageDiagnostics, "dxc (DXIL)", dx.ExitCode, dx.Stdout + "\n" + dx.Stderr); }
                }
                else
                {
                    var glsl = await RunGlslAsync(snapshotPath, spvPath, stage, includeDirectory, cancellationToken).ConfigureAwait(false);
                    stageDiagnostics.AddRange(ParseGlslDiagnostics(glsl.Stdout + "\n" + glsl.Stderr, stage, adapted.PrefixLineCount, diagnosticPaths));
                    if (glsl.ExitCode == 0 && File.Exists(spvPath)) { spirv[stage.Stage] = File.ReadAllBytes(spvPath); }
                    else { AddToolFailure(stageDiagnostics, "glslang", glsl.ExitCode, glsl.Stdout + "\n" + glsl.Stderr); }

                    if (spirv.ContainsKey(stage.Stage))
                    {
                        var hlslPath = $"{stageStem}.translated.hlsl";
                        var cross = await RunToolAsync("spirv-cross", null, ["--hlsl", "--shader-model", "66", spvPath, "--output", hlslPath], cancellationToken).ConfigureAwait(false);
                        if (cross.ExitCode != 0 || !File.Exists(hlslPath))
                        {
                            AddToolFailure(stageDiagnostics, "spirv-cross", cross.ExitCode, cross.Stdout + "\n" + cross.Stderr);
                        }
                        else
                        {
                            // SPIRV-Cross preserves GLSL descriptor numbers in HLSL register annotations (for
                            // example, Vulkan binding 1 becomes t1/s1). The Direct3D backend packs sampled-image
                            // bindings densely in descriptor-list order (t0/s0, t1/s1, ...), while retaining every
                            // declared channel slot even when optimization removes an unused sampler. Normalize the
                            // translated Shadertoy input declarations to that shared ABI before DXC sees them.
                            var translated = File.ReadAllText(hlslPath);
                            var remapped = RemapTranslatedHlslRegisters(translated, descriptor);
                            if (!string.Equals(translated, remapped, StringComparison.Ordinal))
                            {
                                await File.WriteAllTextAsync(hlslPath, remapped, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                            }
                            var dx = await RunDxcAsync(hlslPath, dxilPath, profile, "main", spirv: false, includeDirectory, cancellationToken).ConfigureAwait(false);
                            stageDiagnostics.AddRange(ParseDxcDiagnostics(dx.Stdout + "\n" + dx.Stderr, stage, 0, diagnosticPaths));
                            if (dx.ExitCode == 0 && File.Exists(dxilPath)) { dxil[stage.Stage] = File.ReadAllBytes(dxilPath); }
                            else { AddToolFailure(stageDiagnostics, "dxc (DXIL)", dx.ExitCode, dx.Stdout + "\n" + dx.Stderr); }
                        }
                    }
                }

                diagnostics.AddRange(stageDiagnostics);
                if (stageDiagnostics.Any(static diagnostic => diagnostic.IsError))
                {
                    return new CompiledShader(descriptor.Name, FirstPath(descriptor), sourceHash, spirv, dxil, diagnostics, collected.Dependencies);
                }
            }

            foreach (var stage in descriptor.Stages) {
                var stageStem = Path.Combine(buildRoot, $"{cacheKey}.{buildId}.{StageName(stage.Stage)}");
                File.Move($"{stageStem}.spv.tmp", BytecodePath(stem, stage.Stage, "spv"), true);
                File.Move($"{stageStem}.dxil.tmp", BytecodePath(stem, stage.Stage, "dxil"), true);
            }
            var completeTemp = $"{completePath}.{buildId}.tmp";
            await File.WriteAllTextAsync(completeTemp, sourceHash, cancellationToken).ConfigureAwait(false);
            File.Move(completeTemp, completePath, true);
            return new CompiledShader(descriptor.Name, FirstPath(descriptor), sourceHash, spirv, dxil, diagnostics, collected.Dependencies);
        }
        finally
        {
            try { Directory.Delete(buildRoot, true); } catch (IOException) { }
        }
    }
    private async Task<ShaderProcessResult> RunDxcAsync(string source, string output, string profile, string entry,
        bool spirv, string? includeDirectory, CancellationToken cancellationToken)
    {
        var args = new List<string>();
        if (spirv) { args.AddRange(["-spirv", "-fspv-target-env=vulkan1.3", "-fspv-entrypoint-name=main"]); }
        if (!string.IsNullOrWhiteSpace(includeDirectory)) { args.Add("-I" + includeDirectory); }
        args.AddRange(["-T", profile, "-E", entry, "-Fo", output, source]);
        return await RunToolAsync("dxc", null, args, cancellationToken).ConfigureAwait(false);
    }

    private Task<ShaderProcessResult> RunGlslAsync(string source, string output, ShaderStageSource stage, string? includeDirectory, CancellationToken cancellationToken)
    {
        var args = new List<string> { "-V", "-S", StageName(stage.Stage), "-e", stage.Language == ShaderSourceLanguage.ShadertoyGlsl ? "main" : stage.EntryPoint, "-o", output };
        if (!string.IsNullOrWhiteSpace(includeDirectory)) { args.Add("-I" + includeDirectory); }
        args.Add(source);
        return RunToolAsync("glslangValidator", "glslang", args, cancellationToken);
    }

    private async Task<ShaderProcessResult> RunToolAsync(string name, string? fallback, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var executable = m_toolchain.Directory is null ? name : m_toolchain.Resolve(name, fallback);
        try { return await m_processRunner.RunAsync(executable, args, cancellationToken).ConfigureAwait(false); }
        catch (System.ComponentModel.Win32Exception) when (m_toolchain.Directory is null && fallback is not null)
        {
            try { return await m_processRunner.RunAsync(fallback, args, cancellationToken).ConfigureAwait(false); }
            catch (System.ComponentModel.Win32Exception) { throw new ShaderToolMissingException($"{name} (or {fallback})", null); }
        }
        catch (System.ComponentModel.Win32Exception) { throw new ShaderToolMissingException(name, m_toolchain.Directory); }
    }

    internal static string RemapTranslatedHlslRegisters(string translated, IReadOnlyDictionary<string, uint> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        var descriptors = OrderedChannels(channels)
            .Select(pair => new ShaderDescriptorBinding(pair.Value, GpuComputeBindingKind.SampledImage))
            .ToArray();
        return RemapTranslatedHlslRegisters(translated, descriptors);
    }

    private static string RemapTranslatedHlslRegisters(string translated, ShaderCompilationRequest request) =>
        request.DescriptorBindings.Count == 0
            ? RemapTranslatedHlslRegisters(translated, request.Channels)
            : RemapTranslatedHlslRegisters(translated, request.DescriptorBindings);

    internal static string RemapTranslatedHlslRegisters(string translated, IReadOnlyList<ShaderDescriptorBinding> descriptors)
    {
        ArgumentNullException.ThrowIfNull(translated);
        ArgumentNullException.ThrowIfNull(descriptors);
        if (descriptors.Count == 0) { return translated; }

        var logicalToDenseSrv = new Dictionary<uint, uint>();
        var logicalToDenseUav = new Dictionary<uint, uint>();
        uint nextSrv = 0;
        uint nextUav = 0;
        foreach (var descriptor in descriptors)
        {
            var destination = IsSrv(descriptor.Kind) ? logicalToDenseSrv : logicalToDenseUav;
            var next = IsSrv(descriptor.Kind) ? nextSrv : nextUav;
            destination.TryAdd(descriptor.VulkanBinding, next);
            var increment = descriptor.Count;
            if (IsSrv(descriptor.Kind)) { nextSrv = checked(nextSrv + increment); }
            else { nextUav = checked(nextUav + increment); }
        }

        return HlslRegisterPattern().Replace(translated, match =>
        {
            var kind = match.Groups[1].Value;
            if (!uint.TryParse(match.Groups[2].Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var logical))
            {
                return match.Value;
            }
            var map = kind is "t" or "s" ? logicalToDenseSrv : logicalToDenseUav;
            return map.TryGetValue(logical, out var dense) ? $"register({kind}{dense}, space0)" : match.Value;
        });
    }

    private static bool IsSrv(GpuComputeBindingKind kind) => kind is
        GpuComputeBindingKind.SampledImage or
        GpuComputeBindingKind.StorageBufferRead or
        GpuComputeBindingKind.AccelerationStructure;

    private static IEnumerable<KeyValuePair<string, uint>> OrderedChannels(IReadOnlyDictionary<string, uint> channels) =>
        channels.OrderBy(static pair => ChannelOrdinal(pair.Key).HasValue ? 0 : 1)
            .ThenBy(static pair => ChannelOrdinal(pair.Key) ?? uint.MaxValue)
            .ThenBy(static pair => pair.Key, StringComparer.Ordinal);

    private static uint? ChannelOrdinal(string name)
    {
        const string prefix = "iChannel";
        if (!name.StartsWith(prefix, StringComparison.Ordinal) ||
            !uint.TryParse(name.AsSpan(prefix.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var ordinal))
        {
            return null;
        }
        return ordinal;
    }

    [GeneratedRegex(@"register\(\s*([tsu])(\d+)(?:\s*,\s*space\d+)?\s*\)")]
    private static partial Regex HlslRegisterPattern();
    private static ShadertoyShaderAdapter.AdaptedSource Prepare(ShaderStageSource stage, ShaderCompilationRequest descriptor)
    {
        if (stage.Language == ShaderSourceLanguage.ShadertoyGlsl)
        {
            return ShadertoyShaderAdapter.Adapt(stage.Source, descriptor.Channels, stage.GroupSizeX, stage.GroupSizeY, stage.GroupSizeZ, descriptor.OutputFormat, descriptor.Config);
        }
        return new ShadertoyShaderAdapter.AdaptedSource(stage.Source, 0, []);
    }

    private sealed record CollectedDependencies(
        IReadOnlyList<ShaderSourceDependency> Dependencies,
        IReadOnlyDictionary<string, string> Contents
    );

    private CollectedDependencies CollectDependencies(ShaderCompilationRequest descriptor)
    {
        var result = new List<ShaderSourceDependency>();
        var contents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var stage in descriptor.Stages)
        {
            CollectIncludes(stage.Path, stage.Source, visited, result, contents);
        }
        return new CollectedDependencies(result, contents);
    }

    private static void CollectIncludes(
        string path,
        string source,
        HashSet<string> visited,
        List<ShaderSourceDependency> result,
        Dictionary<string, string> contents)
    {
        foreach (Match match in IncludePattern().Matches(source))
        {
            var includePath = match.Groups[1].Value;
            var fullPath = Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(path)) ?? Environment.CurrentDirectory,
                includePath));
            if (!visited.Add(fullPath) || !File.Exists(fullPath))
            {
                continue;
            }
            var included = File.ReadAllText(fullPath);
            contents[fullPath] = included;
            result.Add(new ShaderSourceDependency(fullPath, Hash(included)));
            CollectIncludes(fullPath, included, visited, result, contents);
        }
    }

    private static string SnapshotPath(string root, string path)
    {
        var fullPath = Path.GetFullPath(path);
        var volume = Path.GetPathRoot(fullPath) ?? string.Empty;
        var relative = volume.Length == 0 ? fullPath : Path.GetRelativePath(volume, fullPath);
        if (Path.IsPathRooted(relative) || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Shader source path '{path}' cannot be represented in a compile snapshot.");
        }

        // Keep volume identity in the snapshot. Without it, equal relative paths
        // on separate drives/UNC roots can overwrite each other's includes.
        var volumeKey = Hash(volume.ToUpperInvariant())[..16];
        return Path.Combine(root, "volume-" + volumeKey, relative);
    }
    private string CreateCacheKey(ShaderCompilationRequest descriptor, IReadOnlyList<ShaderSourceDependency> dependencies)
    {
        var builder = new StringBuilder(CompilerVersion).Append('|').Append(SerializeDescriptor(descriptor)).Append('|').Append(ShadertoyShaderAdapter.Version)
            .Append('|').Append(m_toolchain.Identity);
        foreach (var stage in descriptor.Stages)
        {
            builder.Append('|').Append(stage.Stage).Append('|').Append(Profile(stage.Stage)).Append('|').Append(stage.EntryPoint)
                .Append("|-spirv|-fspv-target-env=vulkan1.3|-fspv-entrypoint-name=main|-T|").Append(Profile(stage.Stage)).Append("|-E|").Append(stage.EntryPoint)
                .Append("|-Fo|").Append(StageName(stage.Stage)).Append(".target|glslangValidator|-V|-S|").Append(StageName(stage.Stage))
                .Append("|-e|").Append(stage.EntryPoint).Append("|spirv-cross|--hlsl|--shader-model|66");
        }
        foreach (var dependency in dependencies.OrderBy(static dependency => dependency.Path, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append('|').Append(dependency.Path).Append('|').Append(dependency.ContentHash);
        }
        return Hash(builder.ToString());
    }
    private static string SerializeDescriptor(ShaderCompilationRequest descriptor) =>
        string.Join("\n", descriptor.Stages.Select(stage => $"{stage.Stage}|{stage.Path}|{stage.Language}|{stage.EntryPoint}|{stage.GroupSizeX},{stage.GroupSizeY},{stage.GroupSizeZ}|{stage.Source}")) +
        string.Join("\n", descriptor.Channels.OrderBy(static pair => pair.Key, StringComparer.Ordinal).Select(static pair => $"channel|{pair.Key}|{pair.Value}")) +
        $"\nformat|{descriptor.OutputFormat}|config|{string.Join(",", descriptor.Config.OrderBy(static pair => pair.Key, StringComparer.Ordinal).Select(static pair => $"{pair.Key}:{pair.Value.Type}"))}" +
        $"\ndescriptors|{string.Join(",", descriptor.DescriptorBindings.Select(static binding => $"{binding.VulkanBinding}:{binding.Kind}:{binding.Count}"))}";
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string FirstPath(ShaderCompilationRequest descriptor) => descriptor.Stages[0].Path;
    private static string BytecodePath(string stem, ShaderStage stage, string target) => $"{stem}.{StageName(stage)}.{target}";
    private static string StageName(ShaderStage stage) => stage switch { ShaderStage.Vertex => "vert", ShaderStage.Fragment => "frag", _ => "comp" };
    private static string Profile(ShaderStage stage) => stage switch { ShaderStage.Vertex => "vs_6_6", ShaderStage.Fragment => "ps_6_6", _ => "cs_6_6" };
    private static void AddToolFailure(List<ShaderDiagnostic> diagnostics, string tool, int exitCode, string output)
    {
        var detail = string.Join(" ", output.Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries).Select(static line => line.Trim()).Where(static line => line.Length != 0).Take(4));
        diagnostics.Add(new ShaderDiagnostic(0, 0, string.IsNullOrEmpty(detail) ? $"{tool} exited with code {exitCode}." : $"{tool} exited with code {exitCode}: {detail}", true));
    }
    private static List<ShaderDiagnostic> ParseGlslDiagnostics(
        string text,
        ShaderStageSource stage,
        int prefix,
        IReadOnlyDictionary<string, string> paths)
    {
        var result = new List<ShaderDiagnostic>();
        foreach (var line in text.Split('\n'))
        {
            var match = GlslDiagnosticPattern().Match(line.TrimEnd('\r'));
            if (match.Success && int.TryParse(match.Groups[3].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lineNumber))
            {
                var path = DiagnosticPath(match.Groups[2].Value, stage, paths);
                var authoredLine = string.Equals(Path.GetFullPath(path), Path.GetFullPath(stage.Path), StringComparison.OrdinalIgnoreCase);
                result.Add(new ShaderDiagnostic(
                    authoredLine ? Math.Max(0, lineNumber - prefix) : lineNumber,
                    0,
                    match.Groups[4].Value.Trim(),
                    match.Groups[1].Value.Equals("ERROR", StringComparison.OrdinalIgnoreCase),
                    stage.Stage,
                    path));
            }
        }
        return result;
    }

    private static List<ShaderDiagnostic> ParseDxcDiagnostics(
        string text,
        ShaderStageSource stage,
        int prefix,
        IReadOnlyDictionary<string, string> paths)
    {
        var result = new List<ShaderDiagnostic>();
        foreach (var line in text.Split('\n'))
        {
            var match = DxcDiagnosticPattern().Match(line.TrimEnd('\r'));
            if (match.Success && int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lineNumber))
            {
                var path = DiagnosticPath(match.Groups[1].Value, stage, paths);
                var authoredLine = string.Equals(Path.GetFullPath(path), Path.GetFullPath(stage.Path), StringComparison.OrdinalIgnoreCase);
                result.Add(new ShaderDiagnostic(
                    authoredLine ? Math.Max(0, lineNumber - prefix) : lineNumber,
                    int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture),
                    match.Groups[5].Value.Trim(),
                    match.Groups[4].Value == "error",
                    stage.Stage,
                    path));
            }
        }
        return result;
    }

    private static string DiagnosticPath(string reported, ShaderStageSource stage, IReadOnlyDictionary<string, string> paths)
    {
        var candidate = reported.Trim();
        if (paths.TryGetValue(candidate, out var authored)) { return authored; }
        try
        {
            var full = Path.GetFullPath(candidate);
            if (paths.TryGetValue(full, out authored)) { return authored; }
        }
        catch (ArgumentException) { }
        return stage.Path;
    }
    [GeneratedRegex(@"^(ERROR|WARNING):\s*(.*):(\d+):\s*(.*)$")]
    private static partial Regex GlslDiagnosticPattern();
    [GeneratedRegex(@"^(.*):(\d+):(\d+):\s*(error|warning):\s*(.*)$")]
    private static partial Regex DxcDiagnosticPattern();
    [GeneratedRegex(@"^\s*#include\s*[<""]([^>""]+)[>""]")]
    private static partial Regex IncludePattern();
}